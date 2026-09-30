use crate::{cancellation::Control, Cancellation};
use std::error::Error;
use std::ffi::c_void;
use std::sync::atomic::Ordering;
use whisper_rs::{
    FullParams, SamplingStrategy, WhisperContext, WhisperContextParameters, WhisperError,
    WhisperState,
};

pub enum DecodeOutcome {
    Completed(Vec<Segment>),
    Cancelled {
        abort_observed: bool,
        native_error: Option<String>,
    },
}

// These callbacks never allocate, lock, log, panic, or touch context/PCM.
unsafe extern "C" fn abort(user_data: *mut c_void) -> bool {
    let control = &*(user_data as *const Control);
    control.checks.fetch_add(1, Ordering::Relaxed);
    let requested = control.requested.load(Ordering::Acquire);
    if requested {
        control.observed.store(true, Ordering::Release);
    }
    requested
}

unsafe extern "C" fn encoder_begin(
    _context: *mut whisper_rs::WhisperSysContext,
    _state: *mut whisper_rs::WhisperSysState,
    user_data: *mut c_void,
) -> bool {
    let control = &*(user_data as *const Control);
    control.encoder_entries.fetch_add(1, Ordering::Relaxed);
    let requested = control.requested.load(Ordering::Acquire);
    if requested {
        control.observed.store(true, Ordering::Release);
    }
    !requested
}

struct Running<'a>(&'a Control);
impl Drop for Running<'_> {
    fn drop(&mut self) {
        self.0.running.store(false, Ordering::Release);
    }
}

pub struct Segment {
    pub start_ms: i64,
    pub end_ms: i64,
    pub text: String,
}

pub struct AsrEngine {
    state: WhisperState,
    _context: WhisperContext,
    threads: i32,
}

impl AsrEngine {
    pub fn load(path: &str, gpu: bool, threads: i32) -> Result<Self, Box<dyn Error>> {
        if !(1..=64).contains(&threads) {
            return Err("threads must be in 1..=64".into());
        }
        if gpu && !cfg!(any(feature = "cuda", feature = "metal")) {
            return Err("GPU requested but no GPU backend is compiled".into());
        }
        let mut parameters = WhisperContextParameters::default();
        parameters.use_gpu(gpu);
        let context = WhisperContext::new_with_params(path, parameters)?;
        let state = context.create_state()?;
        Ok(Self {
            state,
            _context: context,
            threads,
        })
    }

    pub fn transcribe(
        &mut self,
        pcm: &[f32],
        language: &str,
    ) -> Result<Vec<Segment>, Box<dyn Error>> {
        match self.transcribe_cancellable(pcm, language, &Cancellation::default())? {
            DecodeOutcome::Completed(segments) => Ok(segments),
            DecodeOutcome::Cancelled { .. } => {
                Err("unexpected cancellation of unshared token".into())
            }
        }
    }

    pub fn transcribe_cancellable(
        &mut self,
        pcm: &[f32],
        language: &str,
        cancellation: &Cancellation,
    ) -> Result<DecodeOutcome, Box<dyn Error>> {
        if pcm.is_empty()
            || pcm.len() > 16_000 * 120
            || pcm.iter().any(|sample| !sample.is_finite())
        {
            return Err("audio must contain 0..120 seconds of finite 16 kHz mono samples".into());
        }
        if !matches!(language, "en" | "ja" | "ko") {
            return Err("supported probe languages: en, ja, ko".into());
        }
        let control = &*cancellation.0;
        if control.claimed.swap(true, Ordering::AcqRel) {
            return Err(
                "cancellation token already used; create a fresh token for each job".into(),
            );
        }
        if control.requested.load(Ordering::Acquire) {
            return Ok(DecodeOutcome::Cancelled {
                abort_observed: false,
                native_error: None,
            });
        }
        let mut parameters = FullParams::new(SamplingStrategy::Greedy { best_of: 1 });
        parameters.set_n_threads(self.threads);
        parameters.set_language(Some(language));
        parameters.set_translate(false);
        parameters.set_no_context(true);
        parameters.set_print_special(false);
        parameters.set_print_progress(false);
        parameters.set_print_realtime(false);
        parameters.set_print_timestamps(false);
        // SAFETY: Arc storage is stable and borrowed for this synchronous full call.
        // Callbacks only access shared atomics. Native work returns before this borrow
        // ends; the owner never frees state/context or starts another decode meanwhile.
        let user_data = std::sync::Arc::as_ptr(&cancellation.0) as *mut c_void;
        unsafe {
            parameters.set_abort_callback(Some(abort));
            parameters.set_abort_callback_user_data(user_data);
            parameters.set_start_encoder_callback(Some(encoder_begin));
            parameters.set_start_encoder_callback_user_data(user_data);
        }
        control.running.store(true, Ordering::Release);
        let _running = Running(control);
        let result = self.state.full(parameters, pcm);
        let observed = control.observed.load(Ordering::Acquire);
        if let Err(error) = &result {
            // These are the pinned native encode/decode abort return paths. Other
            // failures remain errors even if someone happened to request cancellation.
            if !observed || !matches!(error, WhisperError::GenericError(-6 | -8 | -9)) {
                return Err((*error).into());
            }
        }
        if control.requested.load(Ordering::Acquire) {
            return Ok(DecodeOutcome::Cancelled {
                abort_observed: observed,
                native_error: result.err().map(|error| error.to_string()),
            });
        }
        let mut segments = Vec::new();
        for index in 0..self.state.full_n_segments()? {
            segments.push(Segment {
                start_ms: self.state.full_get_segment_t0(index)? * 10,
                end_ms: self.state.full_get_segment_t1(index)? * 10,
                text: self.state.full_get_segment_text(index)?,
            });
        }
        Ok(DecodeOutcome::Completed(segments))
    }
}

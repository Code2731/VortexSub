//! Whisper owner for diagnostic file/live paths; file I/O and full stay off IPC.
#![cfg_attr(not(feature = "native-asr"), allow(dead_code, unused_imports))]
use echosub_asr_whisper::Cancellation;
use echosub_audio_core::{JobIdentity, SegmentIdentity};
use echosub_pipeline_core::{AsrJob, Outcome};
use sha2::{Digest, Sha256};
use std::io::Read;
use std::path::Path;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    mpsc::{self, Receiver, SyncSender},
    Arc,
};
use std::thread::JoinHandle;
use std::time::Instant;

pub struct ModelConfig {
    pub path: String,
    pub hash: String,
    pub gpu: bool,
    pub threads: i32,
    pub vad: Option<VadConfig>,
}
#[cfg_attr(not(feature = "native-vad"), allow(dead_code))]
#[derive(Clone)]
pub struct VadConfig {
    pub model: String,
    pub model_hash: String,
    pub runtime: String,
    pub runtime_hash: String,
}
pub struct LoadedPcm {
    pub pcm: Vec<f32>,
    pub ranges: Option<Vec<echosub_audio_core::SampleRange>>,
    pub vad_calls: u64,
    pub vad_s: f64,
}
pub struct Input {
    pub id: SegmentIdentity,
    pub path: String,
    pub hash: String,
    pub language: String,
}
pub struct LoadedInput {
    pub id: SegmentIdentity,
    pub language: String,
    pub result: Result<LoadedPcm, &'static str>,
}
pub struct Decode {
    pub job: AsrJob,
    pub language: String,
    pub cancellation: Cancellation,
    pub continued_from: Option<SegmentIdentity>,
}
pub enum Completion {
    Ready {
        load_s: f64,
    },
    Failed,
    Decoded {
        key: JobIdentity,
        outcome: Outcome,
        decode_s: f64,
        abort_observed: bool,
        language: String,
        overlap_segments_removed: usize,
        overlap_tokens_removed: usize,
        timed_token_count: usize,
        input_samples: usize,
        nonzero_samples: usize,
    },
}
pub struct NativeOwner {
    pub inputs: SyncSender<Input>,
    pub loaded: Receiver<LoadedInput>,
    pub jobs: SyncSender<Decode>,
    pub completed: Receiver<Completion>,
    stop: Arc<AtomicBool>,
    threads: Vec<JoinHandle<()>>,
}
pub fn config_from_args(args: &[String]) -> Result<Option<ModelConfig>, &'static str> {
    if !args.iter().any(|s| s == "--diagnostic-asr") {
        if args.iter().any(|s| s == "--diagnostic-vad") {
            return Err("VAD requires diagnostic ASR");
        }
        return Ok(None);
    }
    if !cfg!(feature = "native-asr") {
        return Err("Native ASR is not compiled; use native-asr feature");
    }
    if args.iter().any(|s| s == "--mock-pipeline") {
        return Err("Diagnostic ASR and mock pipeline cannot be combined");
    }
    fn value<'a>(a: &'a [String], name: &str) -> Option<&'a str> {
        a.iter()
            .position(|s| s == name)
            .and_then(|i| a.get(i + 1))
            .map(String::as_str)
    }
    let path = value(args, "--asr-model").ok_or("Missing --asr-model")?;
    let hash = value(args, "--asr-sha256").ok_or("Missing --asr-sha256")?;
    if !Path::new(path).is_absolute() || !valid_hash(hash) {
        return Err("Model path must be absolute and SHA-256 must be explicit");
    }
    let threads = value(args, "--asr-threads")
        .unwrap_or("8")
        .parse::<i32>()
        .map_err(|_| "Invalid threads")?;
    if !(1..=64).contains(&threads) {
        return Err("Threads must be 1..64");
    }
    let gpu = match value(args, "--asr-backend").unwrap_or("cpu") {
        "cpu" => false,
        "cuda" if cfg!(feature = "cuda") => true,
        _ => return Err("Requested backend is not compiled"),
    };
    let vad = if args.iter().any(|s| s == "--diagnostic-vad") {
        if !cfg!(feature = "native-vad") {
            return Err("Native VAD is not compiled");
        }
        let model = value(args, "--vad-model").ok_or("Missing VAD model")?;
        let model_hash = value(args, "--vad-sha256").ok_or("Missing VAD hash")?;
        let runtime = value(args, "--vad-runtime").ok_or("Missing VAD runtime")?;
        let runtime_hash = value(args, "--vad-runtime-sha256").ok_or("Missing runtime hash")?;
        if !Path::new(model).is_absolute()
            || !Path::new(runtime).is_absolute()
            || !valid_hash(model_hash)
            || !valid_hash(runtime_hash)
        {
            return Err("VAD paths must be absolute with explicit hashes");
        }
        Some(VadConfig {
            model: model.into(),
            model_hash: model_hash.into(),
            runtime: runtime.into(),
            runtime_hash: runtime_hash.into(),
        })
    } else {
        None
    };
    Ok(Some(ModelConfig {
        path: path.into(),
        hash: hash.to_ascii_lowercase(),
        gpu,
        threads,
        vad,
    }))
}
pub fn valid_hash(hash: &str) -> bool {
    hash.len() == 64 && hash.bytes().all(|b| b.is_ascii_hexdigit())
}
pub fn load_wav(path: &str, hash: &str) -> Result<Vec<f32>, &'static str> {
    let mut bytes = Vec::new();
    std::fs::File::open(path)
        .map_err(|_| "INPUT_UNAVAILABLE")?
        .take(1024 * 1024 + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| "INPUT_UNAVAILABLE")?;
    if bytes.len() > 1024 * 1024 {
        return Err("INPUT_TOO_LARGE");
    }
    if format!("{:x}", Sha256::digest(&bytes)) != hash.to_ascii_lowercase() {
        return Err("INPUT_HASH_MISMATCH");
    }
    let mut reader =
        hound::WavReader::new(std::io::Cursor::new(bytes)).map_err(|_| "INPUT_FORMAT")?;
    let spec = reader.spec();
    if spec.channels != 1
        || spec.sample_rate != 16000
        || reader.duration() == 0
        || reader.duration() > 128000
    {
        return Err("INPUT_FORMAT");
    }
    let pcm = match (spec.sample_format, spec.bits_per_sample) {
        (hound::SampleFormat::Int, 16) => reader
            .samples::<i16>()
            .map(|s| s.map(|s| s as f32 / 32768.0))
            .collect::<Result<Vec<_>, _>>(),
        (hound::SampleFormat::Float, 32) => reader.samples::<f32>().collect::<Result<Vec<_>, _>>(),
        _ => return Err("INPUT_FORMAT"),
    }
    .map_err(|_| "INPUT_FORMAT")?;
    if pcm.len() != reader.duration() as usize
        || pcm.iter().any(|x| !x.is_finite() || x.abs() > 1.0)
    {
        return Err("INPUT_FORMAT");
    }
    Ok(pcm)
}
impl NativeOwner {
    #[cfg(feature = "native-asr")]
    pub fn start(config: ModelConfig) -> Self {
        let stop = Arc::new(AtomicBool::new(false));
        let (inputs, input_rx) = mpsc::sync_channel::<Input>(1);
        let (loaded_tx, loaded) = mpsc::sync_channel(1);
        let loader_stop = stop.clone();
        let vad_config = config.vad;
        let loader = std::thread::spawn(move || {
            #[cfg(feature = "native-vad")]
            let mut vad = vad_config.map(|v| {
                echosub_vad_silero::native::OnnxBackend::load(
                    Path::new(&v.model),
                    &v.model_hash,
                    Path::new(&v.runtime),
                    &v.runtime_hash,
                )
                .map(|backend| {
                    echosub_vad_silero::Detector::new(
                        backend,
                        echosub_audio_core::AudioIdentity {
                            session_id: 1,
                            epoch: 0,
                        },
                        0,
                    )
                })
            });
            #[cfg(not(feature = "native-vad"))]
            let _ = vad_config;
            while let Ok(input) = input_rx.recv() {
                if loader_stop.load(Ordering::Acquire) {
                    break;
                }
                let result = load_wav(&input.path, &input.hash).and_then(|pcm| {
                    #[allow(unused_mut)]
                    let mut loaded = LoadedPcm {
                        pcm,
                        ranges: None,
                        vad_calls: 0,
                        vad_s: 0.,
                    };
                    #[cfg(feature = "native-vad")]
                    if let Some(vad) = &mut vad {
                        let detector = vad.as_mut().map_err(|_| "VAD_MODEL_FAILED")?;
                        let identity = echosub_audio_core::AudioIdentity {
                            session_id: 1,
                            epoch: input.id.segment_id,
                        };
                        detector
                            .reset(identity, 0)
                            .map_err(|_| "VAD_RESET_FAILED")?;
                        let start = Instant::now();
                        let result = echosub_vad_silero::segment(detector, identity, &loaded.pcm)
                            .map_err(|_| "VAD_INFERENCE_FAILED")?;
                        loaded.vad_s = start.elapsed().as_secs_f64();
                        loaded.vad_calls = result.model_calls;
                        loaded.ranges = Some(result.ranges);
                    }
                    Ok(loaded)
                });
                if loader_stop.load(Ordering::Acquire) {
                    break;
                }
                if loaded_tx
                    .send(LoadedInput {
                        id: input.id,
                        language: input.language,
                        result,
                    })
                    .is_err()
                {
                    break;
                }
            }
        });
        let (jobs, job_rx) = mpsc::sync_channel::<Decode>(1);
        let (done_tx, completed) = mpsc::sync_channel(1);
        let native_stop = stop.clone();
        let native = std::thread::spawn(move || {
            let start = Instant::now();
            let verified = (|| {
                let mut file = std::fs::File::open(&config.path).ok()?;
                let mut digest = Sha256::new();
                let mut buffer = [0u8; 65536];
                loop {
                    if native_stop.load(Ordering::Acquire) {
                        return None;
                    }
                    let n = file.read(&mut buffer).ok()?;
                    if n == 0 {
                        break;
                    }
                    digest.update(&buffer[..n]);
                }
                if format!("{:x}", digest.finalize()) != config.hash {
                    return None;
                }
                echosub_asr_whisper::AsrEngine::load(&config.path, config.gpu, config.threads).ok()
            })();
            let Some(mut engine) = verified else {
                let _ = done_tx.send(Completion::Failed);
                return;
            };
            if native_stop.load(Ordering::Acquire) {
                return;
            }
            if done_tx
                .send(Completion::Ready {
                    load_s: start.elapsed().as_secs_f64(),
                })
                .is_err()
            {
                return;
            }
            let mut reconcile = crate::asr_reconcile::Reconciler::default();
            while let Ok(task) = job_rx.recv() {
                if native_stop.load(Ordering::Acquire) {
                    break;
                }
                let key = task.job.key();
                let start = Instant::now();
                let mut overlap_segments_removed = 0;
                let mut overlap_tokens_removed = 0;
                let mut timed_token_count = 0;
                let input_samples = task.job.pcm.samples().len();
                let nonzero_samples = task.job.pcm.samples().iter().filter(|s| **s != 0.0).count();
                let outcome = if task.cancellation.snapshot().requested {
                    Outcome::Cancelled
                } else if task.job.product_range != task.job.pcm.range() {
                    // Window jobs require complete-source reconstruction. Never publish a tail
                    // as the complete caption while that owner integration is not enabled.
                    Outcome::Failed
                } else if nonzero_samples == 0 {
                    Outcome::NoSpeech
                } else {
                    match engine.transcribe_cancellable_timed(
                        task.job.pcm.samples(),
                        &task.language,
                        &task.cancellation,
                    ) {
                        Ok(echosub_asr_whisper::DecodeOutcome::Completed(segments)) => {
                            let (outcome, removed) = reconcile.finish(
                                SegmentIdentity {
                                    audio: key.audio,
                                    segment_id: key.segment_id,
                                },
                                task.job.pcm.range(),
                                task.job.kind,
                                task.continued_from,
                                segments,
                            );
                            overlap_segments_removed = removed;
                            overlap_tokens_removed = reconcile.tokens_removed;
                            timed_token_count = reconcile.timed_tokens;
                            outcome
                        }
                        Ok(echosub_asr_whisper::DecodeOutcome::Cancelled { .. }) => {
                            Outcome::Cancelled
                        }
                        Err(_) => Outcome::Failed,
                    }
                };
                let abort_observed = task.cancellation.snapshot().abort_observed;
                let decode_s = start.elapsed().as_secs_f64();
                let language = task.language.clone();
                drop(task); // Full returned; release PCM before acknowledging reservation.
                if done_tx
                    .send(Completion::Decoded {
                        key,
                        outcome,
                        decode_s,
                        abort_observed,
                        language,
                        overlap_segments_removed,
                        overlap_tokens_removed,
                        timed_token_count,
                        input_samples,
                        nonzero_samples,
                    })
                    .is_err()
                {
                    break;
                }
            }
            // State/context are dropped on this same owner thread, after full returned.
        });
        Self {
            inputs,
            loaded,
            jobs,
            completed,
            stop,
            threads: vec![loader, native],
        }
    }
    #[cfg(not(feature = "native-asr"))]
    pub fn start(_: ModelConfig) -> Self {
        unreachable!("config parser rejects native mode without feature")
    }
    pub fn request_stop(&self) {
        self.stop.store(true, Ordering::Release);
    }
    pub fn finish(mut self) {
        self.request_stop();
        drop(self.inputs);
        drop(self.jobs);
        drop(self.loaded);
        drop(self.completed);
        for thread in self.threads.drain(..) {
            let _ = thread.join();
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn invalid_native_arguments_are_rejected() {
        assert!(config_from_args(&["--diagnostic-asr".into()]).is_err());
        assert!(config_from_args(&["--diagnostic-vad".into()]).is_err());
        assert!(!valid_hash("bad"));
        assert!(valid_hash(&"f".repeat(64)));
    }
    #[test]
    fn fixture_validation_rejects_hash_format_and_oversized_audio() {
        let path = std::env::temp_dir().join(format!("echosub-fixture-{}.wav", std::process::id()));
        let spec = hound::WavSpec {
            channels: 1,
            sample_rate: 16000,
            bits_per_sample: 16,
            sample_format: hound::SampleFormat::Int,
        };
        let mut writer = hound::WavWriter::create(&path, spec).unwrap();
        for _ in 0..512 {
            writer.write_sample(100i16).unwrap();
        }
        writer.finalize().unwrap();
        let bytes = std::fs::read(&path).unwrap();
        let hash = format!("{:x}", Sha256::digest(&bytes));
        assert_eq!(load_wav(path.to_str().unwrap(), &hash).unwrap().len(), 512);
        assert_eq!(
            load_wav(path.to_str().unwrap(), &"0".repeat(64)),
            Err("INPUT_HASH_MISMATCH")
        );
        for (rate, count) in [(8000, 512), (16000, 128001), (16000, 0)] {
            let mut writer = hound::WavWriter::create(
                &path,
                hound::WavSpec {
                    sample_rate: rate,
                    ..spec
                },
            )
            .unwrap();
            for _ in 0..count {
                writer.write_sample(100i16).unwrap();
            }
            writer.finalize().unwrap();
            let hash = format!("{:x}", Sha256::digest(std::fs::read(&path).unwrap()));
            assert_eq!(load_wav(path.to_str().unwrap(), &hash), Err("INPUT_FORMAT"));
        }
        std::fs::write(&path, vec![0; 1024 * 1024 + 1]).unwrap();
        assert_eq!(
            load_wav(path.to_str().unwrap(), &hash),
            Err("INPUT_TOO_LARGE")
        );
        std::fs::remove_file(path).unwrap();
    }
}

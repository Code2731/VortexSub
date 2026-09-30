//! Continuous VAD owner. Every queue is bounded; stop discards unfinished speech.
#![cfg_attr(not(feature = "native-vad"), allow(dead_code, unused_imports))]
use crate::native_owner::VadConfig;
use echosub_audio_core::{
    AudioFrame, AudioIdentity, CaptureHealth, SpeechEvent, VadSegmenter, VadSettings,
};
use echosub_vad_silero::{Backend, Detector};
use std::sync::{
    atomic::{AtomicBool, AtomicU64, Ordering},
    mpsc::{self, Receiver, SyncSender},
    Arc, Mutex,
};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

pub const CAPACITY: usize = 32;
struct Stream<B: Backend> {
    detector: Detector<B>,
    segmenter: VadSegmenter,
    partial_enabled: bool,
}
impl<B: Backend> Stream<B> {
    fn new(backend: B, identity: AudioIdentity, start: u64) -> Self {
        Self {
            detector: Detector::new(backend, identity, start),
            segmenter: VadSegmenter::new(identity, start, VadSettings::default()).unwrap(),
            partial_enabled: false,
        }
    }
    fn collect(&mut self, events: Vec<SpeechEvent>) -> Vec<SpeechEvent> {
        events
            .into_iter()
            .filter(|e| {
                if matches!(e, SpeechEvent::ModelReset(_)) {
                    self.detector.reset_recurrent();
                }
                matches!(e, SpeechEvent::Final { .. } | SpeechEvent::Discarded { .. })
                    || (self.partial_enabled && matches!(e, SpeechEvent::Partial(_)))
            })
            .collect()
    }
    fn push(&mut self, frame: &AudioFrame, now: u64) -> Result<Vec<SpeechEvent>, &'static str> {
        let p = self
            .detector
            .probability(frame.identity, frame.range, &frame.samples)
            .map_err(|_| "LIVE_VAD_INFERENCE_FAILED")?;
        let p = VadSegmenter::requires_probability(&frame.samples).then_some(p);
        let events = self
            .segmenter
            .push(frame, p, now)
            .map_err(|_| "LIVE_VAD_RANGE_FAILED")?;
        Ok(self.collect(events))
    }
    fn poll(&mut self, now: u64) -> Result<Vec<SpeechEvent>, &'static str> {
        let events = self
            .segmenter
            .poll(now, CaptureHealth::Healthy)
            .map_err(|_| "LIVE_VAD_CLOCK_FAILED")?;
        Ok(self.collect(events))
    }
}
pub struct ResultBatch {
    pub identity: AudioIdentity,
    pub events: Vec<SpeechEvent>,
}
#[derive(Default)]
struct Shared {
    stop: AtomicBool,
    ready: AtomicBool,
    error: Mutex<Option<&'static str>>,
    frames: AtomicU64,
    calls: AtomicU64,
    vad_ns: AtomicU64,
}
pub struct LiveOwner {
    pub input: SyncSender<AudioFrame>,
    pub output: Receiver<ResultBatch>,
    shared: Arc<Shared>,
    thread: Option<JoinHandle<()>>,
}
impl LiveOwner {
    #[cfg(feature = "native-vad")]
    pub fn start(config: VadConfig, identity: AudioIdentity, partial_enabled: bool) -> Self {
        let shared = Arc::new(Shared::default());
        let (input, rx) = mpsc::sync_channel::<AudioFrame>(CAPACITY);
        let (tx, output) = mpsc::sync_channel(CAPACITY);
        let s = shared.clone();
        let thread = std::thread::spawn(move || {
            let result = (|| -> Result<(), &'static str> {
                let backend = echosub_vad_silero::native::OnnxBackend::load(
                    std::path::Path::new(&config.model),
                    &config.model_hash,
                    std::path::Path::new(&config.runtime),
                    &config.runtime_hash,
                )
                .map_err(|_| "LIVE_VAD_MODEL_FAILED")?;
                s.ready.store(true, Ordering::Release);
                let clock = Instant::now();
                let mut backend = Some(backend);
                let mut stream = None;
                while !s.stop.load(Ordering::Acquire) {
                    let frame = match rx.recv_timeout(Duration::from_millis(20)) {
                        Ok(frame) => Some(frame),
                        Err(mpsc::RecvTimeoutError::Timeout) => None,
                        Err(_) => break,
                    };
                    if s.stop.load(Ordering::Acquire) {
                        break;
                    }
                    let now = clock.elapsed().as_nanos().min(u64::MAX as u128) as u64;
                    let started = Instant::now();
                    let events = if let Some(frame) = frame {
                        if frame.identity != identity {
                            return Err("LIVE_VAD_STALE_INPUT");
                        }
                        let stream = stream.get_or_insert_with(|| {
                            let mut stream =
                                Stream::new(backend.take().unwrap(), identity, frame.range.start);
                            stream.partial_enabled = partial_enabled;
                            stream
                        });
                        let events = stream.push(&frame, now)?;
                        s.frames.fetch_add(1, Ordering::Relaxed);
                        s.calls.store(stream.detector.calls(), Ordering::Relaxed);
                        s.vad_ns.fetch_add(
                            started.elapsed().as_nanos().min(u64::MAX as u128) as u64,
                            Ordering::Relaxed,
                        );
                        events
                    } else if let Some(stream) = &mut stream {
                        stream.poll(now)?
                    } else {
                        Vec::new()
                    };
                    if s.stop.load(Ordering::Acquire) {
                        break;
                    }
                    if !events.is_empty() {
                        tx.try_send(ResultBatch { identity, events })
                            .map_err(|_| "LIVE_VAD_OUTPUT_OVERFLOW")?;
                    }
                }
                // No close/flush: Stop, fault and EOF discard the active segment.
                Ok(())
            })();
            if let Err(code) = result {
                *s.error.lock().unwrap() = Some(code);
                s.stop.store(true, Ordering::Release);
            }
        });
        Self {
            input,
            output,
            shared,
            thread: Some(thread),
        }
    }
    #[cfg(not(feature = "native-vad"))]
    pub fn start(_: VadConfig, _: AudioIdentity, _: bool) -> Self {
        unreachable!("CLI requires native-vad")
    }
    pub fn stop(&self) {
        self.shared.stop.store(true, Ordering::Release);
    }
    pub fn finished(&self) -> bool {
        self.thread.as_ref().is_none_or(|t| t.is_finished())
    }
    pub fn error(&self) -> Option<&'static str> {
        *self.shared.error.lock().unwrap()
    }
    pub fn state(&self) -> serde_json::Value {
        serde_json::json!({"ready":self.shared.ready.load(Ordering::Acquire),"frames":self.shared.frames.load(Ordering::Relaxed),"model_calls":self.shared.calls.load(Ordering::Relaxed),"vad_s":self.shared.vad_ns.load(Ordering::Relaxed) as f64/1e9,"error":self.error(),"queue_capacity":CAPACITY,"awaiting_join":!self.finished()})
    }
}
impl Drop for LiveOwner {
    fn drop(&mut self) {
        self.stop();
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use echosub_audio_core::{DiscardReason, SampleRange};
    struct Voiced {
        calls: u32,
    }
    impl Backend for Voiced {
        fn infer(
            &mut self,
            _: &[f32; 576],
            state: &[f32; 256],
        ) -> Result<(f32, [f32; 256]), echosub_vad_silero::Error> {
            assert_eq!(
                state[0], self.calls as f32,
                "recurrence must survive packets"
            );
            self.calls += 1;
            Ok((0.9, [self.calls as f32; 256]))
        }
    }
    const ID: AudioIdentity = AudioIdentity {
        session_id: 1,
        epoch: 2,
    };
    fn frame(i: u64, value: f32) -> AudioFrame {
        AudioFrame {
            identity: ID,
            range: SampleRange {
                start: 16000 + i * 512,
                end: 16000 + (i + 1) * 512,
            },
            samples: [value; 512],
        }
    }
    #[test]
    fn recurrence_survives_packets_and_final_resets_without_padding_pcm() {
        let mut s = Stream::new(Voiced { calls: 0 }, ID, 16000);
        for i in 0..10 {
            assert!(s.push(&frame(i, 0.2), i * 32_000_000).unwrap().is_empty());
        }
        assert_eq!(s.detector.calls(), 10);
        let mut finals = Vec::new();
        for i in 10..26 {
            finals.extend(s.push(&frame(i, 0.), i * 32_000_000).unwrap());
        }
        let SpeechEvent::Final { segment, .. } = finals[0] else {
            panic!("expected final")
        };
        assert!(segment.pcm_range.start >= 16000 && segment.pcm_range.end <= 16000 + 26 * 512);
        assert_eq!(s.detector.calls(), 10);
    }
    #[test]
    fn partials_are_opt_in_and_share_the_final_vad_identity() {
        for enabled in [false, true] {
            let mut s = Stream::new(Voiced { calls: 0 }, ID, 16000);
            s.partial_enabled = enabled;
            let mut events = Vec::new();
            for i in 0..80 {
                events.extend(s.push(&frame(i, 0.2), i * 32_000_000).unwrap());
            }
            for i in 80..96 {
                events.extend(s.push(&frame(i, 0.), i * 32_000_000).unwrap());
            }
            let partials: Vec<_> = events
                .iter()
                .filter_map(|e| match e {
                    SpeechEvent::Partial(s) => Some(s),
                    _ => None,
                })
                .collect();
            let final_id = events
                .iter()
                .find_map(|e| match e {
                    SpeechEvent::Final { segment, .. } => Some(segment.id),
                    _ => None,
                })
                .unwrap();
            if enabled {
                assert!(partials.len() >= 2);
                assert!(partials.iter().all(|s| s.id == final_id));
                assert!(partials
                    .windows(2)
                    .all(|w| w[0].pcm_range.end < w[1].pcm_range.end));
            } else {
                assert!(partials.is_empty());
            }
        }
    }
    #[test]
    fn stop_discards_active_speech_and_stale_frames_are_rejected() {
        let mut s = Stream::new(Voiced { calls: 0 }, ID, 16000);
        for i in 0..10 {
            s.push(&frame(i, 0.2), i * 32_000_000).unwrap();
        }
        let events = s
            .segmenter
            .interrupt(DiscardReason::Stop, 400_000_000)
            .unwrap();
        assert!(events.iter().any(|e| matches!(
            e,
            SpeechEvent::Discarded {
                reason: DiscardReason::Stop,
                ..
            }
        )));
        assert!(!events
            .iter()
            .any(|e| matches!(e, SpeechEvent::Final { .. })));
        let mut old = frame(10, 0.2);
        old.identity.epoch -= 1;
        assert!(s.push(&old, 500_000_000).is_err());
    }
}

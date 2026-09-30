use crate::native_owner::{Completion, Decode, Input, ModelConfig, NativeOwner};
use crate::transport::Outbox;
use echosub_asr_whisper::Cancellation;
use echosub_audio_core::JobIdentity;
use echosub_audio_core::{
    AudioIdentity, RollingAudio, SampleRange, SegmentIdentity, SnapshotPool, MAX_ROLLING_SAMPLES,
    MAX_SNAPSHOT_SAMPLES,
};
use echosub_pipeline_core::{AsrKind, CoreError, Outcome, Pipeline, Record, TranslationJob};
use serde_json::{json, Value};
use std::time::Instant;
pub type Reply = Result<Value, (&'static str, &'static str)>;
pub struct Runtime {
    pub core: Pipeline,
    ring: RollingAudio,
    pool: SnapshotPool,
    next_segment: u64,
    pub enabled: bool,
    epoch: AudioIdentity,
    origin: Instant,
    translation: Option<TranslationJob>,
    native: Option<NativeOwner>,
    model_state: &'static str,
    pending_inputs: Vec<SegmentIdentity>,
    languages: Vec<(JobIdentity, String)>,
    flight: Option<(JobIdentity, Cancellation)>,
    load_s: Option<f64>,
    completed_jobs: u64,
}
impl Runtime {
    pub fn new(enabled: bool, config: Option<ModelConfig>) -> Self {
        let epoch = AudioIdentity {
            session_id: 1,
            epoch: 1,
        };
        Self {
            core: if config.is_some() {
                Pipeline::new_asr_only(epoch, 1000).unwrap()
            } else {
                Pipeline::new(epoch, 1000, "en", "ko").unwrap()
            },
            ring: RollingAudio::new(MAX_ROLLING_SAMPLES, epoch, 0).unwrap(),
            pool: SnapshotPool::new(4, MAX_SNAPSHOT_SAMPLES).unwrap(),
            next_segment: 1,
            enabled,
            epoch,
            origin: Instant::now(),
            translation: None,
            model_state: if config.is_some() {
                "Preparing"
            } else {
                "NotInstalled"
            },
            native: config.map(NativeOwner::start),
            pending_inputs: Vec::new(),
            languages: Vec::new(),
            flight: None,
            load_s: None,
            completed_jobs: 0,
        }
    }
    pub fn implementation(&self) -> &str {
        if self.native.is_some() {
            "native-asr-fixture"
        } else {
            "mock"
        }
    }
    pub fn has_native(&self) -> bool {
        self.native.is_some()
    }
    pub fn state(&self, q: &Outbox) -> Value {
        json!({"session":{"session_id":null,"epoch":0,"state":"Idle","elapsed_ms":0},"translator":{"state":"Unavailable"},"model":{"state":self.model_state},"last_seq":q.last_seq(),"history_version":self.core.version(),"diagnostic_asr":{"enabled":self.has_native(),"epoch":self.epoch.epoch,"pending_inputs":self.pending_inputs.len(),"decoding":self.flight.is_some(),"native_running":self.flight.as_ref().is_some_and(|(_,token)|token.snapshot().running),"completed_jobs":self.completed_jobs,"model_load_s":self.load_s,"vad":false}})
    }
    pub fn fixture(&mut self, method: &str, p: &Value, q: &Outbox) -> Reply {
        if self.native.is_none() {
            return Err((
                "UNSUPPORTED_CAPABILITY",
                "Diagnostic native ASR is not enabled",
            ));
        }
        match method {
            "transcribe_fixture" => {
                if self.model_state != "Ready" {
                    return Err(("INVALID_STATE", "Model is not ready"));
                }
                let path = p
                    .get("path")
                    .and_then(Value::as_str)
                    .filter(|s| s.len() <= 4096 && std::path::Path::new(s).is_absolute())
                    .ok_or(("INVALID_REQUEST", "Fixture path must be absolute"))?;
                let hash = p
                    .get("sha256")
                    .and_then(Value::as_str)
                    .filter(|s| crate::native_owner::valid_hash(s))
                    .ok_or(("INVALID_REQUEST", "Fixture SHA-256 is required"))?;
                let language = p
                    .get("language")
                    .and_then(Value::as_str)
                    .filter(|s| matches!(*s, "en" | "ja" | "ko"))
                    .ok_or(("INVALID_REQUEST", "Unsupported fixture language"))?;
                if self.pending_inputs.len() == 2 {
                    return Err(("BUSY", "Two fixture inputs are already pending"));
                }
                let id = SegmentIdentity {
                    audio: self.epoch,
                    segment_id: self.next_segment,
                };
                let input = Input {
                    id,
                    path: path.into(),
                    hash: hash.into(),
                    language: language.into(),
                };
                self.native
                    .as_ref()
                    .unwrap()
                    .inputs
                    .try_send(input)
                    .map_err(|_| ("BUSY", "Fixture loader is occupied"))?;
                self.next_segment += 1;
                self.pending_inputs.push(id);
                Ok(
                    json!({"accepted":true,"session_id":id.audio.session_id,"epoch":id.audio.epoch,"segment_id":id.segment_id,"input":"wav_fixture","vad":false}),
                )
            }
            "reset_fixture_epoch" => {
                self.epoch.epoch = self
                    .epoch
                    .epoch
                    .checked_add(1)
                    .ok_or(("INTERNAL_ERROR", "Epoch exhausted"))?;
                self.core
                    .restart(self.epoch, false, self.now())
                    .map_err(core_error)?;
                if let Some((_, token)) = &self.flight {
                    token.request();
                }
                self.languages.clear();
                self.ring
                    .reset(self.epoch, self.ring.retained_range().end)
                    .map_err(|_| ("INTERNAL_ERROR", "Ring reset failed"))?;
                q.publish(
                    "history.changed",
                    json!({"history_version":self.core.version(),"reason":"epoch_changed"}),
                    None,
                )
                .map_err(|_| ("INTERNAL_ERROR", "Event unavailable"))?;
                Ok(
                    json!({"accepted":true,"epoch":self.epoch.epoch,"awaiting_native_return":self.flight.is_some()}),
                )
            }
            _ => Err(("UNSUPPORTED_CAPABILITY", "Unknown fixture method")),
        }
    }
    fn poll_native(&mut self, q: &Outbox) -> std::io::Result<()> {
        if self.native.is_none() {
            return Ok(());
        }
        loop {
            let completion = match self.native.as_ref().unwrap().completed.try_recv() {
                Ok(completion) => completion,
                Err(std::sync::mpsc::TryRecvError::Empty) => break,
                Err(std::sync::mpsc::TryRecvError::Disconnected)
                    if self.model_state == "Failed" =>
                {
                    break
                }
                Err(_) => return Err(std::io::Error::other("Native owner exited unexpectedly")),
            };
            match completion {
                Completion::Ready { load_s } => {
                    self.model_state = "Ready";
                    self.load_s = Some(load_s);
                    q.publish("model.state",json!({"state":"Ready","model_load_s":load_s,"implementation":self.implementation()}),None)?;
                }
                Completion::Failed => {
                    self.model_state = "Failed";
                    q.publish(
                        "model.state",
                        json!({"state":"Failed","code":"MODEL_LOAD_FAILED"}),
                        None,
                    )?;
                }
                Completion::Decoded {
                    key,
                    outcome,
                    decode_s,
                    abort_observed,
                } => {
                    self.flight = None;
                    let applied = self
                        .core
                        .complete_asr(key, outcome, self.now())
                        .map_err(|_| std::io::Error::other("ASR completion rejected"))?;
                    self.completed_jobs += 1;
                    q.publish("asr.completed",json!({"session_id":key.audio.session_id,"epoch":key.audio.epoch,"segment_id":key.segment_id,"source_revision":key.source_revision,"decode_s":decode_s,"applied":applied==echosub_pipeline_core::Apply::Applied,"abort_observed":abort_observed}),None)?;
                    if applied == echosub_pipeline_core::Apply::Applied {
                        let state = self
                            .core
                            .record(SegmentIdentity {
                                audio: key.audio,
                                segment_id: key.segment_id,
                            })
                            .unwrap()
                            .source_state;
                        let event = match state {
                            echosub_pipeline_core::SourceState::Final => "source.final",
                            echosub_pipeline_core::SourceState::Skipped => "segment.skipped",
                            _ => "segment.failed",
                        };
                        self.changed_record(key.segment_id, event, q)
                            .map_err(|_| std::io::Error::other("ASR record event unavailable"))?;
                    }
                }
            }
        }
        loop {
            let input = match self.native.as_ref().unwrap().loaded.try_recv() {
                Ok(input) => input,
                Err(std::sync::mpsc::TryRecvError::Empty) => break,
                Err(_) => return Err(std::io::Error::other("Fixture loader exited unexpectedly")),
            };
            self.pending_inputs.retain(|id| *id != input.id);
            if input.id.audio != self.epoch {
                q.publish(
                    "fixture.cancelled",
                    json!({"epoch":input.id.audio.epoch,"segment_id":input.id.segment_id}),
                    None,
                )?;
                continue;
            }
            match input.result {
                Err(code) => {
                    q.publish("fixture.failed",json!({"epoch":input.id.audio.epoch,"segment_id":input.id.segment_id,"code":code}),None)?;
                }
                Ok(pcm) => {
                    let start = self.ring.retained_range().end;
                    self.ring
                        .append(self.epoch, start, &pcm)
                        .map_err(|_| std::io::Error::other("Fixture PCM rejected"))?;
                    if !echosub_audio_core::VadSegmenter::requires_probability(&pcm) {
                        q.publish("fixture.suppressed",json!({"epoch":input.id.audio.epoch,"segment_id":input.id.segment_id,"reason":"digital_silence","audio_duration_s":pcm.len() as f64/16000.0}),None)?;
                        continue;
                    }
                    let now = self.now();
                    let admitted = self
                        .core
                        .submit_asr(
                            input.id,
                            SampleRange {
                                start,
                                end: start + pcm.len() as u64,
                            },
                            AsrKind::Final,
                            &self.ring,
                            &mut self.pool,
                            now,
                        )
                        .map_err(|_| std::io::Error::other("Fixture admission rejected"))?;
                    if admitted.queued {
                        self.languages.push((admitted.key, input.language));
                    } else {
                        self.changed_record(input.id.segment_id, "segment.skipped", q)
                            .map_err(|_| std::io::Error::other("Skip record event unavailable"))?;
                    }
                }
            }
        }
        if self.model_state == "Ready" && self.flight.is_none() {
            if let Some(job) = self.core.next_asr() {
                let key = job.key();
                let i = self
                    .languages
                    .iter()
                    .position(|(k, _)| *k == key)
                    .ok_or_else(|| std::io::Error::other("Missing ASR language"))?;
                let language = self.languages.remove(i).1;
                let token = Cancellation::default();
                self.native
                    .as_ref()
                    .unwrap()
                    .jobs
                    .try_send(Decode {
                        job,
                        language,
                        cancellation: token.clone(),
                    })
                    .map_err(|_| std::io::Error::other("Native owner is unavailable"))?;
                self.flight = Some((key, token));
                q.publish("asr.started",json!({"epoch":key.audio.epoch,"segment_id":key.segment_id,"source_revision":key.source_revision}),None)?;
            }
        }
        Ok(())
    }
    pub fn finish(&mut self) {
        self.interrupt();
        if let Some(native) = self.native.take() {
            native.finish();
        }
    }
    fn now(&self) -> u64 {
        self.origin.elapsed().as_nanos().min(u64::MAX as u128) as u64
    }
    pub fn poll(&mut self, q: &Outbox) -> std::io::Result<()> {
        self.poll_native(q)?;
        let before = self.core.version();
        self.core
            .poll(self.now())
            .map_err(|_| std::io::Error::other("Pipeline clock error"))?;
        if self.core.version() != before {
            q.publish(
                "history.changed",
                json!({"history_version":self.core.version()}),
                None,
            )?;
        }
        Ok(())
    }
    pub fn interrupt(&mut self) {
        let _ = self.core.interrupt(self.now());
        if let Some((_, token)) = &self.flight {
            token.request();
        }
        if let Some(native) = &self.native {
            native.request_stop();
        }
    }
    pub fn history(&self, p: &Value, q: &Outbox) -> Reply {
        fn number(p: &Value, key: &str, default: u64) -> Result<u64, (&'static str, &'static str)> {
            match p.get(key) {
                None => Ok(default),
                Some(v) => v
                    .as_u64()
                    .ok_or(("INVALID_REQUEST", "Expected nonnegative integer")),
            }
        }
        let version = number(p, "expected_version", self.core.version())?;
        let offset = number(p, "offset", 0)?;
        let limit = number(p, "limit", 4)?;
        if offset > 1000 || !(1..=4).contains(&limit) {
            return Err(("INVALID_REQUEST", "History offset/limit is out of range"));
        }
        let page = self
            .core
            .history_page(version, offset as usize, limit as usize)
            .map_err(core_error)?;
        Ok(
            json!({"history_version":page.version,"records":page.records.iter().map(record).collect::<Vec<_>>(),"next_offset":page.next_offset,"last_seq":q.last_seq(),"implementation":self.implementation()}),
        )
    }
    pub fn mock(&mut self, method: &str, p: &Value, q: &Outbox) -> Reply {
        if !self.enabled {
            return Err((
                "UNSUPPORTED_CAPABILITY",
                "Mock pipeline requires --mock-pipeline",
            ));
        }
        match method {
            "mock_segment" | "mock_burst" => {
                let source = p
                    .get("source")
                    .and_then(Value::as_str)
                    .filter(|s| !s.trim().is_empty() && s.len() <= 4096 && !s.contains('\0'))
                    .ok_or(("INVALID_REQUEST", "Invalid mock source"))?;
                let count = if method == "mock_burst" {
                    p.get("count")
                        .and_then(Value::as_u64)
                        .filter(|n| (1..=1100).contains(n))
                        .ok_or(("INVALID_REQUEST", "Mock burst count must be 1..1100"))?
                } else {
                    1
                };
                for _ in 0..count {
                    self.segment(source, q)?;
                }
                Ok(
                    json!({"accepted":true,"history_version":self.core.version(),"last_seq":q.last_seq(),"implementation":self.implementation()}),
                )
            }
            "mock_translate" => {
                let job = self
                    .translation
                    .as_ref()
                    .ok_or(("INVALID_STATE", "No mock translation in flight"))?;
                if p.get("translation_request_id").and_then(Value::as_u64)
                    != Some(job.key.request_id)
                    || p.get("session_id").and_then(Value::as_u64)
                        != Some(job.key.source.audio.session_id)
                    || p.get("epoch").and_then(Value::as_u64) != Some(job.key.source.audio.epoch)
                    || p.get("source_revision").and_then(Value::as_u64)
                        != Some(job.key.source.source_revision)
                    || p.get("segment_id").and_then(Value::as_u64)
                        != Some(job.key.source.segment_id)
                {
                    return Err(("STALE_RESULT", "Mock translation key does not match"));
                }
                let text = p
                    .get("text")
                    .and_then(Value::as_str)
                    .filter(|s| !s.trim().is_empty() && s.len() <= 4096 && !s.contains('\0'))
                    .ok_or(("INVALID_REQUEST", "Invalid mock translation"))?;
                let key = job.key;
                self.core
                    .complete_translation(key, Outcome::Text(text.to_owned()), self.now())
                    .map_err(core_error)?;
                self.translation = None;
                self.changed_record(key.source.segment_id, "translation.updated", q)?;
                self.translation = self.core.next_translation(self.now()).map_err(core_error)?;
                Ok(
                    json!({"accepted":true,"history_version":self.core.version(),"implementation":self.implementation()}),
                )
            }
            _ => Err(("UNSUPPORTED_CAPABILITY", "Unknown mock method")),
        }
    }
    fn segment(&mut self, source: &str, q: &Outbox) -> Reply {
        let start = self.ring.retained_range().end;
        self.ring
            .append(self.epoch, start, &[0.25; 512])
            .map_err(|_| ("INTERNAL_ERROR", "Mock PCM unavailable"))?;
        let id = SegmentIdentity {
            audio: self.epoch,
            segment_id: self.next_segment,
        };
        self.next_segment += 1;
        let now = self.now();
        self.core
            .submit_asr(
                id,
                SampleRange {
                    start,
                    end: start + 512,
                },
                AsrKind::Final,
                &self.ring,
                &mut self.pool,
                now,
            )
            .map_err(core_error)?;
        let job = self
            .core
            .next_asr()
            .ok_or(("INTERNAL_ERROR", "Mock ASR unavailable"))?;
        self.core
            .complete_asr(job.key(), Outcome::Text(source.to_owned()), now)
            .map_err(core_error)?;
        self.changed_record(id.segment_id, "source.final", q)?;
        if self.translation.is_none() {
            self.translation = self.core.next_translation(self.now()).map_err(core_error)?;
        }
        Ok(json!({}))
    }
    fn changed_record(&self, id: u64, name: &str, q: &Outbox) -> Reply {
        let r = self
            .core
            .record(SegmentIdentity {
                audio: self.epoch,
                segment_id: id,
            })
            .ok_or(("INTERNAL_ERROR", "Record unavailable"))?;
        q.publish(
            name,
            json!({"history_version":self.core.version(),"record":record(r)}),
            None,
        )
        .map_err(|_| ("INTERNAL_ERROR", "Event unavailable"))?;
        Ok(json!({}))
    }
}
fn core_error(error: CoreError) -> (&'static str, &'static str) {
    match error {
        CoreError::StaleSnapshot => ("STALE_SNAPSHOT", "History changed; restart at offset zero"),
        _ => ("INVALID_STATE", "Pipeline operation rejected"),
    }
}
pub fn record(r: &Record) -> Value {
    json!({"session_id":r.key.audio.session_id,"epoch":r.key.audio.epoch,"segment_id":r.key.segment_id,"source_revision":r.key.source_revision,"applied_source_revision":r.applied_source_revision,"audio_start_sample":r.range.start,"audio_end_sample":r.range.end,"audio_start_s":r.range.start_s(),"audio_end_s":r.range.end_s(),"source_state":format!("{:?}",r.source_state),"source":r.source,"source_reason":r.source_reason.map(|s|format!("{s:?}")),"translation_state":format!("{:?}",r.translation_state),"translation":r.translation,"translation_reason":r.translation_reason.map(|s|format!("{s:?}")),"translation_request_id":r.translation_request_id})
}

impl Drop for Runtime {
    fn drop(&mut self) {
        self.finish();
    }
}

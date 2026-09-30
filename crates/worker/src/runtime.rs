use crate::transport::Outbox;
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
}
impl Runtime {
    pub fn new(enabled: bool) -> Self {
        let epoch = AudioIdentity {
            session_id: 1,
            epoch: 1,
        };
        Self {
            core: Pipeline::new(epoch, 1000, "en", "ko").unwrap(),
            ring: RollingAudio::new(MAX_ROLLING_SAMPLES, epoch, 0).unwrap(),
            pool: SnapshotPool::new(4, MAX_SNAPSHOT_SAMPLES).unwrap(),
            next_segment: 1,
            enabled,
            epoch,
            origin: Instant::now(),
            translation: None,
        }
    }
    fn now(&self) -> u64 {
        self.origin.elapsed().as_nanos().min(u64::MAX as u128) as u64
    }
    pub fn poll(&mut self, q: &Outbox) -> std::io::Result<()> {
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
            json!({"history_version":page.version,"records":page.records.iter().map(record).collect::<Vec<_>>(),"next_offset":page.next_offset,"last_seq":q.last_seq(),"implementation":"mock"}),
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
                    json!({"accepted":true,"history_version":self.core.version(),"last_seq":q.last_seq(),"implementation":"mock"}),
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
                    json!({"accepted":true,"history_version":self.core.version(),"implementation":"mock"}),
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
            .ok_or(("INTERNAL_ERROR", "Mock record unavailable"))?;
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

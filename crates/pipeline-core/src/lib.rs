//! Deterministic processing-thread state. No native decode, HTTP, IPC or UI.
use echosub_audio_core::{
    AudioError, AudioIdentity, JobIdentity, PcmSnapshot, RollingAudio, SampleRange,
    SegmentIdentity, SnapshotPool,
};
use std::collections::VecDeque;

pub const MAX_HISTORY: usize = 1000;
pub const MAX_TEXT_BYTES: usize = 4096;
pub const TRANSLATION_DEADLINE_NS: u64 = 8_000_000_000;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CoreError {
    StaleIdentity,
    Closed,
    InvalidRange,
    InvalidText,
    InvalidConfig,
    HistoryFull,
    Frozen,
    ClockRegression,
    Overflow,
    StaleSnapshot,
    UnknownJob,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Reason {
    QueueFull,
    SnapshotUnavailable,
    DecodeFailed,
    InvalidText,
    Cancelled,
    Interrupted,
    EpochChanged,
    Deadline,
    Superseded,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SourceState {
    Partial,
    FinalPending,
    Final,
    Failed,
    Skipped,
    Discarded,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TranslationState {
    None,
    Pending,
    Done,
    Failed,
    Skipped,
    Bypassed,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Record {
    pub key: JobIdentity,
    pub applied_source_revision: Option<u64>,
    pub range: SampleRange,
    pub source_state: SourceState,
    pub source: String,
    pub source_reason: Option<Reason>,
    pub translation_state: TranslationState,
    pub translation: String,
    pub translation_reason: Option<Reason>,
    pub translation_request_id: Option<u64>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AsrKind {
    Partial,
    Final,
}
pub struct AsrJob {
    pub kind: AsrKind,
    pub pcm: PcmSnapshot,
}
impl AsrJob {
    pub fn key(&self) -> JobIdentity {
        self.pcm.key()
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TranslationKey {
    pub source: JobIdentity,
    pub request_id: u64,
}
#[derive(Clone, Debug)]
pub struct TranslationJob {
    pub key: TranslationKey,
    pub source: String,
    pub context: Vec<String>,
    pub source_language: String,
    pub target_language: String,
    pub deadline_ns: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Admission {
    pub key: JobIdentity,
    pub queued: bool,
    pub cancel_asr: Option<JobIdentity>,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Cancellation {
    pub asr: Option<JobIdentity>,
    pub translation: Option<TranslationKey>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Apply {
    Applied,
    Ignored,
}
pub enum Outcome {
    Text(String),
    Failed,
    Cancelled,
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HistoryPage {
    pub version: u64,
    pub records: Vec<Record>,
    pub next_offset: Option<usize>,
}
#[derive(Clone, Copy)]
struct AsrFlight {
    key: JobIdentity,
    kind: AsrKind,
    cancelled: bool,
}
#[derive(Clone, Copy)]
struct TranslationFlight {
    key: TranslationKey,
    deadline: u64,
    cancelled: bool,
}

/// Single owner. Leased PCM belongs to dispatched jobs until actual native return.
/// Cancellation changes eligibility, never releases the single-flight reservation.
pub struct Pipeline {
    identity: AudioIdentity,
    running: bool,
    clock: u64,
    version: u64,
    capacity: usize,
    records: VecDeque<Record>,
    final_queue: VecDeque<AsrJob>,
    partial: Option<AsrJob>,
    asr: Option<AsrFlight>,
    translation_queue: VecDeque<TranslationJob>,
    translation: Option<TranslationFlight>,
    next_request: u64,
    last_segment_id: u64,
    source_language: String,
    target_language: String,
}
impl Pipeline {
    pub fn new(
        identity: AudioIdentity,
        history_capacity: usize,
        source_language: &str,
        target_language: &str,
    ) -> Result<Self, CoreError> {
        fn language(s: &str) -> bool {
            !s.is_empty()
                && s.len() <= 35
                && s.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')
        }
        if history_capacity == 0
            || history_capacity > MAX_HISTORY
            || !language(source_language)
            || !language(target_language)
        {
            return Err(CoreError::InvalidConfig);
        }
        Ok(Self {
            identity,
            running: true,
            clock: 0,
            version: 0,
            capacity: history_capacity,
            records: VecDeque::with_capacity(history_capacity),
            final_queue: VecDeque::with_capacity(2),
            partial: None,
            asr: None,
            translation_queue: VecDeque::with_capacity(2),
            translation: None,
            next_request: 1,
            last_segment_id: 0,
            source_language: source_language.to_ascii_lowercase(),
            target_language: target_language.to_ascii_lowercase(),
        })
    }
    fn time(&mut self, now: u64) -> Result<(), CoreError> {
        if now < self.clock {
            return Err(CoreError::ClockRegression);
        }
        self.clock = now;
        Ok(())
    }
    fn changed(&mut self) {
        self.version = self
            .version
            .checked_add(1)
            .expect("history version exhausted");
    }
    fn index(&self, id: SegmentIdentity) -> Option<usize> {
        self.records
            .iter()
            .position(|r| r.key.audio == id.audio && r.key.segment_id == id.segment_id)
    }
    fn key_index(&self, key: JobIdentity) -> Option<usize> {
        self.index(SegmentIdentity {
            audio: key.audio,
            segment_id: key.segment_id,
        })
        .filter(|&i| self.records[i].key == key)
    }
    fn valid_text(s: &str) -> bool {
        !s.trim().is_empty() && s.len() <= MAX_TEXT_BYTES && !s.contains('\0')
    }
    pub fn version(&self) -> u64 {
        self.version
    }
    pub fn record(&self, id: SegmentIdentity) -> Option<&Record> {
        self.index(id).map(|i| &self.records[i])
    }
    pub fn queue_lengths(&self) -> (usize, usize, usize) {
        (
            self.final_queue.len(),
            usize::from(self.partial.is_some()),
            self.translation_queue.len(),
        )
    }
    pub fn cancellation(&self) -> Cancellation {
        Cancellation {
            asr: self.asr.filter(|f| f.cancelled).map(|f| f.key),
            translation: self.translation.filter(|f| f.cancelled).map(|f| f.key),
        }
    }
    pub fn history_page(
        &self,
        expected_version: u64,
        offset: usize,
        limit: usize,
    ) -> Result<HistoryPage, CoreError> {
        if expected_version != self.version {
            return Err(CoreError::StaleSnapshot);
        }
        if limit == 0 || limit > 100 || offset > self.records.len() {
            return Err(CoreError::InvalidConfig);
        }
        let end = (offset + limit).min(self.records.len());
        Ok(HistoryPage {
            version: self.version,
            records: self
                .records
                .iter()
                .skip(offset)
                .take(end - offset)
                .cloned()
                .collect(),
            next_offset: (end < self.records.len()).then_some(end),
        })
    }
    fn room(&mut self) -> Result<(), CoreError> {
        if self.records.len() < self.capacity {
            return Ok(());
        }
        let evict = self
            .records
            .iter()
            .position(|r| {
                !matches!(
                    r.source_state,
                    SourceState::Partial | SourceState::FinalPending
                ) && r.translation_state != TranslationState::Pending
            })
            .ok_or(CoreError::HistoryFull)?;
        self.records.remove(evict);
        self.changed();
        Ok(())
    }
    /// Copy actual PCM into the shared pool after releasing a replaced partial.
    /// Final admission failures become explicit skipped records; no silent loss.
    pub fn submit_asr(
        &mut self,
        id: SegmentIdentity,
        range: SampleRange,
        kind: AsrKind,
        ring: &RollingAudio,
        pool: &mut SnapshotPool,
        now: u64,
    ) -> Result<Admission, CoreError> {
        if !self.running {
            return Err(CoreError::Closed);
        }
        if id.audio != self.identity {
            return Err(CoreError::StaleIdentity);
        }
        if range.start >= range.end || range.end - range.start > 128000 {
            return Err(CoreError::InvalidRange);
        }
        if now < self.clock {
            return Err(CoreError::ClockRegression);
        }
        let previous = self.index(id);
        if previous.is_none() && id.segment_id <= self.last_segment_id {
            return Err(CoreError::StaleIdentity);
        }
        if previous.is_some_and(|i| self.records[i].source_state != SourceState::Partial) {
            return Err(CoreError::Frozen);
        }
        let revision = previous
            .map_or(Some(1), |i| {
                self.records[i].key.source_revision.checked_add(1)
            })
            .ok_or(CoreError::Overflow)?;
        if let Some(i) = previous {
            if range.start != self.records[i].range.start
                || (kind == AsrKind::Partial && range.end < self.records[i].range.end)
            {
                return Err(CoreError::InvalidRange);
            }
        }
        if previous.is_none() {
            self.room()?;
        }
        self.time(now)?;
        let key = JobIdentity {
            audio: id.audio,
            segment_id: id.segment_id,
            source_revision: revision,
        };
        if kind == AsrKind::Partial {
            self.partial = None;
            for r in &mut self.records {
                if r.source_state == SourceState::Partial
                    && (r.key.audio != id.audio || r.key.segment_id != id.segment_id)
                {
                    r.source_state = SourceState::Discarded;
                    r.source_reason = Some(Reason::Superseded);
                }
            }
        } else if self
            .partial
            .as_ref()
            .is_some_and(|p| p.key().audio == id.audio && p.key().segment_id == id.segment_id)
        {
            self.partial = None;
        }
        let i = self.index(id).unwrap_or_else(|| {
            self.last_segment_id = id.segment_id;
            self.records.push_back(Record {
                key,
                applied_source_revision: None,
                range,
                source_state: SourceState::Partial,
                source: String::new(),
                source_reason: None,
                translation_state: TranslationState::None,
                translation: String::new(),
                translation_reason: None,
                translation_request_id: None,
            });
            self.records.len() - 1
        });
        self.records[i].key = key;
        self.records[i].range = range;
        self.records[i].source_reason = None;
        let full = kind == AsrKind::Final && self.final_queue.len() == 2;
        let snapshot = if full {
            Err(AudioError::ResourceExhausted)
        } else {
            pool.snapshot(ring, key, range)
        };
        let queued = snapshot.is_ok();
        match snapshot {
            Ok(pcm) => {
                let job = AsrJob { kind, pcm };
                if kind == AsrKind::Final {
                    self.records[i].source_state = SourceState::FinalPending;
                    self.final_queue.push_back(job);
                } else {
                    self.partial = Some(job);
                }
            }
            Err(_) => {
                self.records[i].source_reason = Some(if full {
                    Reason::QueueFull
                } else {
                    Reason::SnapshotUnavailable
                });
                if kind == AsrKind::Final {
                    self.records[i].source_state = SourceState::Skipped;
                }
            }
        }
        if kind == AsrKind::Final {
            if let Some(f) = self.asr.as_mut().filter(|f| f.kind == AsrKind::Partial) {
                f.cancelled = true;
            }
        }
        self.changed();
        Ok(Admission {
            key,
            queued,
            cancel_asr: self.cancellation().asr,
        })
    }
    pub fn next_asr(&mut self) -> Option<AsrJob> {
        if !self.running || self.asr.is_some() {
            return None;
        }
        let job = self
            .final_queue
            .pop_front()
            .or_else(|| self.partial.take())?;
        self.asr = Some(AsrFlight {
            key: job.key(),
            kind: job.kind,
            cancelled: false,
        });
        Some(job)
    }
    pub fn complete_asr(
        &mut self,
        key: JobIdentity,
        outcome: Outcome,
        now: u64,
    ) -> Result<Apply, CoreError> {
        if self.asr.is_none_or(|f| f.key != key) {
            return Err(CoreError::UnknownJob);
        }
        self.poll(now)?;
        let flight = self.asr.take().unwrap();
        if flight.cancelled || !self.running || key.audio != self.identity {
            return Ok(Apply::Ignored);
        }
        let Some(i) = self.key_index(key) else {
            return Ok(Apply::Ignored);
        };
        let expected = if flight.kind == AsrKind::Final {
            SourceState::FinalPending
        } else {
            SourceState::Partial
        };
        if self.records[i].source_state != expected {
            return Ok(Apply::Ignored);
        }
        match outcome {
            Outcome::Text(text) if Self::valid_text(&text) => {
                // Do not retain a caller's arbitrarily oversized String capacity.
                self.records[i].source = text.into_boxed_str().into_string();
                self.records[i].applied_source_revision = Some(key.source_revision);
                self.records[i].source_reason = None;
                if flight.kind == AsrKind::Final {
                    self.records[i].source_state = SourceState::Final;
                    self.enqueue_translation(i, now);
                }
            }
            other => {
                let reason = match other {
                    Outcome::Cancelled => Reason::Cancelled,
                    Outcome::Text(_) => Reason::InvalidText,
                    _ => Reason::DecodeFailed,
                };
                self.records[i].source_reason = Some(reason);
                if flight.kind == AsrKind::Final {
                    self.records[i].source_state = if reason == Reason::Cancelled {
                        SourceState::Skipped
                    } else {
                        SourceState::Failed
                    };
                }
            }
        }
        self.changed();
        Ok(Apply::Applied)
    }
    fn enqueue_translation(&mut self, i: usize, now: u64) {
        if self.source_language == self.target_language {
            self.records[i].translation_state = TranslationState::Bypassed;
            return;
        }
        if self.translation_queue.len() == 2 {
            self.records[i].translation_state = TranslationState::Skipped;
            self.records[i].translation_reason = Some(Reason::QueueFull);
            return;
        }
        let Some(deadline_ns) = now.checked_add(TRANSLATION_DEADLINE_NS) else {
            self.records[i].translation_state = TranslationState::Failed;
            self.records[i].translation_reason = Some(Reason::Deadline);
            return;
        };
        let Some(next) = self.next_request.checked_add(1) else {
            self.records[i].translation_state = TranslationState::Failed;
            self.records[i].translation_reason = Some(Reason::DecodeFailed);
            return;
        };
        let key = TranslationKey {
            source: self.records[i].key,
            request_id: self.next_request,
        };
        self.next_request = next;
        let context = self
            .records
            .iter()
            .take(i)
            .rev()
            .filter(|r| r.key.audio == key.source.audio && r.source_state == SourceState::Final)
            .take(2)
            .map(|r| r.source.clone())
            .collect::<Vec<_>>()
            .into_iter()
            .rev()
            .collect();
        self.translation_queue.push_back(TranslationJob {
            key,
            source: self.records[i].source.clone(),
            context,
            source_language: self.source_language.clone(),
            target_language: self.target_language.clone(),
            deadline_ns,
        });
        self.records[i].translation_state = TranslationState::Pending;
        self.records[i].translation_request_id = Some(key.request_id);
    }
    fn translation_index(&self, key: TranslationKey) -> Option<usize> {
        self.key_index(key.source).filter(|&i| {
            self.records[i].translation_request_id == Some(key.request_id)
                && self.records[i].translation_state == TranslationState::Pending
        })
    }
    fn skip_translation(&mut self, key: TranslationKey, reason: Reason) {
        if let Some(i) = self.translation_index(key) {
            self.records[i].translation_state = TranslationState::Skipped;
            self.records[i].translation_reason = Some(reason);
            self.changed();
        }
    }
    pub fn poll(&mut self, now: u64) -> Result<Cancellation, CoreError> {
        self.time(now)?;
        while self
            .translation_queue
            .front()
            .is_some_and(|j| now >= j.deadline_ns)
        {
            let job = self.translation_queue.pop_front().unwrap();
            self.skip_translation(job.key, Reason::Deadline);
        }
        if let Some(f) = self
            .translation
            .filter(|f| now >= f.deadline && !f.cancelled)
        {
            self.skip_translation(f.key, Reason::Deadline);
            self.translation.as_mut().unwrap().cancelled = true;
        }
        Ok(self.cancellation())
    }
    pub fn next_translation(&mut self, now: u64) -> Result<Option<TranslationJob>, CoreError> {
        self.poll(now)?;
        if !self.running || self.translation.is_some() {
            return Ok(None);
        }
        let Some(job) = self.translation_queue.pop_front() else {
            return Ok(None);
        };
        self.translation = Some(TranslationFlight {
            key: job.key,
            deadline: job.deadline_ns,
            cancelled: false,
        });
        Ok(Some(job))
    }
    pub fn complete_translation(
        &mut self,
        key: TranslationKey,
        outcome: Outcome,
        now: u64,
    ) -> Result<Apply, CoreError> {
        if self.translation.is_none_or(|f| f.key != key) {
            return Err(CoreError::UnknownJob);
        }
        self.poll(now)?;
        let flight = self.translation.take().unwrap();
        if flight.cancelled || !self.running || key.source.audio != self.identity {
            return Ok(Apply::Ignored);
        }
        let Some(i) = self.translation_index(key) else {
            return Ok(Apply::Ignored);
        };
        match outcome {
            Outcome::Text(text) if Self::valid_text(&text) => {
                self.records[i].translation = text.into_boxed_str().into_string();
                self.records[i].translation_state = TranslationState::Done;
            }
            Outcome::Cancelled => {
                self.records[i].translation_state = TranslationState::Skipped;
                self.records[i].translation_reason = Some(Reason::Cancelled);
            }
            other => {
                self.records[i].translation_state = TranslationState::Failed;
                self.records[i].translation_reason = Some(if matches!(other, Outcome::Text(_)) {
                    Reason::InvalidText
                } else {
                    Reason::DecodeFailed
                });
            }
        }
        self.changed();
        Ok(Apply::Applied)
    }
    pub fn discard_segment(&mut self, id: SegmentIdentity, now: u64) -> Result<(), CoreError> {
        if id.audio != self.identity {
            return Err(CoreError::StaleIdentity);
        }
        if self
            .index(id)
            .is_some_and(|i| self.records[i].source_state != SourceState::Partial)
        {
            return Err(CoreError::Frozen);
        }
        self.time(now)?;
        if let Some(i) = self.index(id) {
            self.records[i].source_state = SourceState::Discarded;
            self.records[i].source_reason = Some(Reason::Interrupted);
            self.partial = self
                .partial
                .take()
                .filter(|p| p.key().audio != id.audio || p.key().segment_id != id.segment_id);
            if let Some(f) = self
                .asr
                .as_mut()
                .filter(|f| f.key.audio == id.audio && f.key.segment_id == id.segment_id)
            {
                f.cancelled = true;
            }
            self.changed();
        }
        Ok(())
    }
    fn invalidate(&mut self, reason: Reason) {
        self.final_queue.clear();
        self.partial = None;
        self.translation_queue.clear();
        if let Some(f) = self.asr.as_mut() {
            f.cancelled = true;
        }
        if let Some(f) = self.translation.as_mut() {
            f.cancelled = true;
        }
        for r in &mut self.records {
            if matches!(
                r.source_state,
                SourceState::Partial | SourceState::FinalPending
            ) {
                r.source_state = SourceState::Discarded;
                r.source_reason = Some(reason);
            }
            if r.translation_state == TranslationState::Pending {
                r.translation_state = TranslationState::Skipped;
                r.translation_reason = Some(reason);
            }
        }
        self.changed();
    }
    /// Pause and Stop share interruption semantics; records remain exportable.
    pub fn interrupt(&mut self, now: u64) -> Result<Cancellation, CoreError> {
        self.time(now)?;
        self.invalidate(Reason::Interrupted);
        self.running = false;
        Ok(self.cancellation())
    }
    /// Caller obtains the user's retain/export/clear choice before a new session.
    /// Old native reservations remain occupied until their completion is acknowledged.
    pub fn restart(
        &mut self,
        identity: AudioIdentity,
        clear_history: bool,
        now: u64,
    ) -> Result<Cancellation, CoreError> {
        if identity.session_id < self.identity.session_id
            || (identity.session_id == self.identity.session_id
                && identity.epoch <= self.identity.epoch)
        {
            return Err(CoreError::StaleIdentity);
        }
        if clear_history && identity.session_id == self.identity.session_id {
            return Err(CoreError::InvalidConfig);
        }
        self.time(now)?;
        self.invalidate(Reason::EpochChanged);
        self.identity = identity;
        self.last_segment_id = 0;
        self.running = true;
        if clear_history {
            self.records.clear();
        }
        Ok(self.cancellation())
    }
}

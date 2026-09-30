//! Probability-driven segmentation; does not contain a trained VAD model.
use crate::{
    AudioError, AudioFrame, AudioGap, AudioIdentity, AudioTail, GapReason, SampleRange,
    FRAME_SAMPLES, MAX_SNAPSHOT_SAMPLES, SAMPLE_RATE,
};
use std::collections::VecDeque;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct VadSettings {
    pub speech_threshold: f32,
    pub minimum_speech_s: f64,
    pub pre_roll_s: f64,
    pub post_roll_s: f64,
    pub ending_silence_s: f64,
    pub max_chunk_s: f64,
    pub overlap_s: f64,
    pub partial_minimum_speech_s: f64,
    pub partial_interval_s: f64,
}
impl Default for VadSettings {
    fn default() -> Self {
        Self {
            speech_threshold: 0.5,
            minimum_speech_s: 0.160,
            pre_roll_s: 0.300,
            post_roll_s: 0.200,
            ending_silence_s: 0.480,
            max_chunk_s: 8.0,
            overlap_s: 0.600,
            partial_minimum_speech_s: 0.800,
            partial_interval_s: 1.0,
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct EffectiveVadSettings {
    pub minimum_speech_samples: u64,
    pub pre_roll_samples: u64,
    pub post_roll_samples: u64,
    pub ending_silence_samples: u64,
    pub max_chunk_samples: u64,
    pub overlap_samples: u64,
    pub partial_minimum_speech_samples: u64,
    pub partial_interval_samples: u64,
}
impl VadSettings {
    pub fn effective(self) -> Result<EffectiveVadSettings, AudioError> {
        if !self.speech_threshold.is_finite() || !(0.0..=1.0).contains(&self.speech_threshold) {
            return Err(AudioError::InvalidVadConfig);
        }
        fn round(s: f64, zero: bool) -> Result<u64, AudioError> {
            if !s.is_finite() || !(0.0..=12.0).contains(&s) {
                return Err(AudioError::InvalidVadConfig);
            }
            let frames = (s * SAMPLE_RATE as f64 / FRAME_SAMPLES as f64).round() as u64;
            if !zero && frames == 0 {
                return Err(AudioError::InvalidVadConfig);
            }
            Ok(frames * FRAME_SAMPLES as u64)
        }
        let e = EffectiveVadSettings {
            minimum_speech_samples: round(self.minimum_speech_s, false)?,
            pre_roll_samples: round(self.pre_roll_s, true)?,
            post_roll_samples: round(self.post_roll_s, true)?,
            ending_silence_samples: round(self.ending_silence_s, false)?,
            max_chunk_samples: round(self.max_chunk_s, false)?,
            overlap_samples: round(self.overlap_s, true)?,
            partial_minimum_speech_samples: round(self.partial_minimum_speech_s, false)?,
            partial_interval_samples: round(self.partial_interval_s, false)?,
        };
        if self.max_chunk_s > 8.0
            || e.max_chunk_samples > MAX_SNAPSHOT_SAMPLES as u64
            || e.pre_roll_samples >= e.max_chunk_samples
            || e.overlap_samples >= e.max_chunk_samples
            || e.minimum_speech_samples > e.max_chunk_samples - e.pre_roll_samples
            || e.partial_minimum_speech_samples > e.max_chunk_samples - e.pre_roll_samples
            || e.partial_minimum_speech_samples < e.minimum_speech_samples
            || e.post_roll_samples > e.ending_silence_samples
        {
            return Err(AudioError::InvalidVadConfig);
        }
        Ok(e)
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SegmentIdentity {
    pub audio: AudioIdentity,
    pub segment_id: u64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SegmentInfo {
    pub id: SegmentIdentity,
    pub pcm_range: SampleRange,
    pub voice_range: SampleRange,
    pub voiced_samples: u64,
    pub new_voiced_samples: u64,
    pub continued_from: Option<SegmentIdentity>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FinalReason {
    Silence,
    ChunkLimit,
    PacketStopped,
    EndOfStream,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DiscardReason {
    TooShort,
    Pause,
    Stop,
    CaptureFault,
    Gap(GapReason),
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VadResetReason {
    DigitalSilence,
    Finalized,
    Watchdog,
    Gap,
    Interrupted,
    EndOfStream,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SpeechEvent {
    Started(SegmentIdentity),
    Partial(SegmentInfo),
    Final {
        segment: SegmentInfo,
        reason: FinalReason,
    },
    Discarded {
        segment: SegmentInfo,
        reason: DiscardReason,
    },
    Gap(AudioGap),
    ModelReset(VadResetReason),
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CaptureHealth {
    Healthy,
    Faulted,
}
#[derive(Clone, Copy)]
struct Decision {
    range: SampleRange,
    voiced: bool,
}
#[derive(Clone, Copy)]
struct Active {
    info: SegmentInfo,
    last_partial_end: Option<u64>,
}
#[derive(Clone, Copy)]
struct Continuation {
    parent: SegmentIdentity,
    end: u64,
    last_voice_end: u64,
}

/// One processing-thread owner. Inputs carry real sample ranges and an injected
/// monotonic observation clock; polling never advances the audio sample cursor.
pub struct VadSegmenter {
    identity: AudioIdentity,
    requested: VadSettings,
    effective: EffectiveVadSettings,
    cursor: u64,
    eligible_start: u64,
    last_now_ns: u64,
    last_packet_ns: Option<u64>,
    next_id: u64,
    history: VecDeque<Decision>,
    active: Option<Active>,
    continuation: Option<Continuation>,
    model_dirty: bool,
    closed: bool,
}
impl VadSegmenter {
    pub fn new(
        identity: AudioIdentity,
        origin: u64,
        settings: VadSettings,
    ) -> Result<Self, AudioError> {
        Ok(Self {
            identity,
            requested: settings,
            effective: settings.effective()?,
            cursor: origin,
            eligible_start: origin,
            last_now_ns: 0,
            last_packet_ns: None,
            next_id: 1,
            history: VecDeque::with_capacity(MAX_SNAPSHOT_SAMPLES / FRAME_SAMPLES),
            active: None,
            continuation: None,
            model_dirty: false,
            closed: false,
        })
    }
    pub fn requested_settings(&self) -> VadSettings {
        self.requested
    }
    pub fn effective_settings(&self) -> EffectiveVadSettings {
        self.effective
    }
    pub fn cursor(&self) -> u64 {
        self.cursor
    }
    pub fn active_segment(&self) -> Option<SegmentInfo> {
        self.active.map(|x| x.info)
    }
    /// Call before model inference; exact digital silence needs no probability.
    pub fn requires_probability(samples: &[f32]) -> bool {
        samples.iter().any(|x| *x != 0.0)
    }
    fn check_clock(&self, now: u64) -> Result<(), AudioError> {
        if now < self.last_now_ns {
            return Err(AudioError::ClockRegression);
        }
        Ok(())
    }
    fn validate(
        &self,
        identity: AudioIdentity,
        range: SampleRange,
        samples: &[f32],
        probability: Option<f32>,
        now: u64,
        tail: bool,
    ) -> Result<bool, AudioError> {
        if self.closed {
            return Err(AudioError::ClosedStream);
        }
        self.check_clock(now)?;
        if identity != self.identity {
            return Err(AudioError::StaleIdentity);
        }
        if range.start != self.cursor {
            return Err(AudioError::NonContiguous);
        }
        if samples.is_empty()
            || if tail {
                samples.len() >= FRAME_SAMPLES
            } else {
                samples.len() != FRAME_SAMPLES
            }
            || range.end.checked_sub(range.start) != Some(samples.len() as u64)
        {
            return Err(AudioError::InvalidRange);
        }
        if samples.iter().any(|x| !x.is_finite()) {
            return Err(AudioError::NonFiniteSample);
        }
        if samples.iter().any(|x| !(-1.0..=1.0).contains(x)) {
            return Err(AudioError::InvalidAmplitude);
        }
        if probability.is_some_and(|p| !p.is_finite() || !(0.0..=1.0).contains(&p)) {
            return Err(AudioError::InvalidProbability);
        }
        let nonzero = Self::requires_probability(samples);
        if nonzero && probability.is_none() {
            return Err(AudioError::InvalidProbability);
        }
        let voiced = nonzero && probability.unwrap_or(0.0) >= self.requested.speech_threshold;
        if voiced && self.active.is_none() && self.next_id == u64::MAX {
            return Err(AudioError::TimestampOverflow);
        }
        Ok(voiced)
    }
    pub fn push(
        &mut self,
        frame: &AudioFrame,
        probability: Option<f32>,
        now_ns: u64,
    ) -> Result<Vec<SpeechEvent>, AudioError> {
        let voiced = self.validate(
            frame.identity,
            frame.range,
            &frame.samples,
            probability,
            now_ns,
            false,
        )?;
        Ok(self.observe(
            frame.range,
            voiced,
            Self::requires_probability(&frame.samples),
            now_ns,
            true,
        ))
    }
    fn reset_model(&mut self, reason: VadResetReason, force: bool, events: &mut Vec<SpeechEvent>) {
        if self.model_dirty || force {
            events.push(SpeechEvent::ModelReset(reason));
        }
        self.model_dirty = false;
    }
    fn observe(
        &mut self,
        range: SampleRange,
        voiced: bool,
        nonzero: bool,
        now: u64,
        partial: bool,
    ) -> Vec<SpeechEvent> {
        let mut events = Vec::new();
        self.last_now_ns = now;
        self.last_packet_ns = Some(now);
        self.cursor = range.end;
        if nonzero {
            self.model_dirty = true;
        } else {
            self.reset_model(VadResetReason::DigitalSilence, false, &mut events);
        }
        if self.history.len() == MAX_SNAPSHOT_SAMPLES / FRAME_SAMPLES {
            self.history.pop_front();
        }
        self.history.push_back(Decision { range, voiced });
        if self.continuation.is_some_and(|c| {
            range.start.saturating_sub(c.last_voice_end) >= self.effective.ending_silence_samples
        }) {
            self.continuation = None;
        }
        if self.active.is_none() && voiced {
            let continuation = self.continuation.take();
            let start = match continuation {
                Some(c) => c.end.saturating_sub(self.effective.overlap_samples),
                None => range
                    .start
                    .saturating_sub(self.effective.pre_roll_samples)
                    .max(self.eligible_start),
            }
            .max(self.history.front().unwrap().range.start);
            let mut info = SegmentInfo {
                id: SegmentIdentity {
                    audio: self.identity,
                    segment_id: self.next_id,
                },
                pcm_range: SampleRange {
                    start,
                    end: range.end,
                },
                voice_range: range,
                voiced_samples: 0,
                new_voiced_samples: 0,
                continued_from: continuation.map(|c| c.parent),
            };
            self.next_id += 1;
            for decision in &self.history {
                if decision.voiced && decision.range.start >= start {
                    if info.voiced_samples == 0 {
                        info.voice_range.start = decision.range.start;
                    }
                    info.voice_range.end = decision.range.end;
                    let n = decision.range.end - decision.range.start;
                    info.voiced_samples += n;
                    if continuation.is_none_or(|c| decision.range.start >= c.end) {
                        info.new_voiced_samples += n;
                    }
                }
            }
            self.active = Some(Active {
                info,
                last_partial_end: None,
            });
            events.push(SpeechEvent::Started(info.id));
        } else if let Some(active) = self.active.as_mut() {
            active.info.pcm_range.end = range.end;
            if voiced {
                active.info.voice_range.end = range.end;
                active.info.voiced_samples += range.end - range.start;
                active.info.new_voiced_samples += range.end - range.start;
            }
        }
        if let Some(active) = self.active {
            let quiet = self.cursor - active.info.voice_range.end;
            if !voiced && quiet >= self.effective.ending_silence_samples {
                self.finalize(FinalReason::Silence, &mut events);
            } else if self.cursor - active.info.pcm_range.start >= self.effective.max_chunk_samples
            {
                self.finalize(FinalReason::ChunkLimit, &mut events);
            } else if partial
                && active.info.new_voiced_samples >= self.effective.partial_minimum_speech_samples
                && active
                    .last_partial_end
                    .is_none_or(|end| self.cursor - end >= self.effective.partial_interval_samples)
            {
                self.active.as_mut().unwrap().last_partial_end = Some(self.cursor);
                events.push(SpeechEvent::Partial(active.info));
            }
        }
        events
    }
    fn finalize(&mut self, reason: FinalReason, events: &mut Vec<SpeechEvent>) {
        let Some(mut active) = self.active.take() else {
            return;
        };
        // Silence/watchdog cannot append missing PCM; post-roll stops at real input.
        active.info.pcm_range.end = active
            .info
            .voice_range
            .end
            .saturating_add(self.effective.post_roll_samples)
            .min(self.cursor);
        let qualified = active.info.voiced_samples >= self.effective.minimum_speech_samples
            && active.info.new_voiced_samples > 0;
        if qualified {
            events.push(SpeechEvent::Final {
                segment: active.info,
                reason,
            });
            if reason == FinalReason::ChunkLimit {
                self.continuation = Some(Continuation {
                    parent: active.info.id,
                    end: active.info.pcm_range.end,
                    last_voice_end: active.info.voice_range.end,
                });
            }
        } else {
            events.push(SpeechEvent::Discarded {
                segment: active.info,
                reason: DiscardReason::TooShort,
            });
            self.continuation = None;
        }
        self.eligible_start = active.info.pcm_range.end;
        self.reset_model(VadResetReason::Finalized, false, events);
    }
    /// Inject monotonic processing-clock time after draining available PCM.
    /// A fault discards the active segment and closes the stream, never finalizes it.
    pub fn poll(
        &mut self,
        now_ns: u64,
        health: CaptureHealth,
    ) -> Result<Vec<SpeechEvent>, AudioError> {
        if self.closed {
            return Err(AudioError::ClosedStream);
        }
        self.check_clock(now_ns)?;
        if health == CaptureHealth::Faulted {
            return self.interrupt(DiscardReason::CaptureFault, now_ns);
        }
        self.last_now_ns = now_ns;
        let mut events = Vec::new();
        if let (Some(active), Some(last)) = (self.active, self.last_packet_ns) {
            let known_quiet = self.cursor - active.info.voice_range.end;
            let missing = (now_ns - last) as u128 * SAMPLE_RATE as u128 / 1_000_000_000;
            if known_quiet as u128 + missing >= self.effective.ending_silence_samples as u128 {
                self.finalize(FinalReason::PacketStopped, &mut events);
                self.continuation = None;
                self.history.clear();
                self.eligible_start = self.cursor;
                self.reset_model(VadResetReason::Watchdog, false, &mut events);
            }
        } else if let (Some(c), Some(last)) = (self.continuation, self.last_packet_ns) {
            let missing = (now_ns - last) as u128 * SAMPLE_RATE as u128 / 1_000_000_000;
            if self.cursor.saturating_sub(c.last_voice_end) as u128 + missing
                >= self.effective.ending_silence_samples as u128
            {
                self.continuation = None;
                self.history.clear();
                self.eligible_start = self.cursor;
                self.reset_model(VadResetReason::Watchdog, false, &mut events);
            }
        }
        Ok(events)
    }
    pub fn close(&mut self, now_ns: u64) -> Result<Vec<SpeechEvent>, AudioError> {
        if self.closed {
            return Err(AudioError::ClosedStream);
        }
        self.check_clock(now_ns)?;
        self.last_now_ns = now_ns;
        let mut events = Vec::new();
        self.finalize(FinalReason::EndOfStream, &mut events);
        self.continuation = None;
        self.history.clear();
        self.closed = true;
        self.reset_model(VadResetReason::EndOfStream, true, &mut events);
        Ok(events)
    }
    /// The caller may zero-pad a tail for model inference, but only real samples
    /// contribute to speech duration, ranges, and snapshot PCM.
    pub fn close_with_tail(
        &mut self,
        tail: &AudioTail,
        probability: Option<f32>,
        now_ns: u64,
    ) -> Result<Vec<SpeechEvent>, AudioError> {
        let voiced = self.validate(
            tail.identity,
            tail.range,
            &tail.samples,
            probability,
            now_ns,
            true,
        )?;
        let mut events = self.observe(
            tail.range,
            voiced,
            Self::requires_probability(&tail.samples),
            now_ns,
            false,
        );
        events.extend(self.close(now_ns)?);
        Ok(events)
    }
    pub fn interrupt(
        &mut self,
        reason: DiscardReason,
        now_ns: u64,
    ) -> Result<Vec<SpeechEvent>, AudioError> {
        if !matches!(
            reason,
            DiscardReason::Pause | DiscardReason::Stop | DiscardReason::CaptureFault
        ) {
            return Err(AudioError::InvalidVadConfig);
        }
        if self.closed {
            return Err(AudioError::ClosedStream);
        }
        self.check_clock(now_ns)?;
        self.last_now_ns = now_ns;
        let mut events = Vec::new();
        if let Some(active) = self.active.take() {
            events.push(SpeechEvent::Discarded {
                segment: active.info,
                reason,
            });
        }
        self.continuation = None;
        self.history.clear();
        self.closed = true;
        self.reset_model(VadResetReason::Interrupted, true, &mut events);
        Ok(events)
    }
    pub fn reset(&mut self, gap: AudioGap, now_ns: u64) -> Result<Vec<SpeechEvent>, AudioError> {
        self.check_clock(now_ns)?;
        if gap.old_identity != self.identity
            || gap.new_identity.session_id != self.identity.session_id
            || gap.new_identity.epoch <= self.identity.epoch
        {
            return Err(AudioError::StaleIdentity);
        }
        if gap.range.start != self.cursor || gap.range.end < gap.range.start {
            return Err(AudioError::NonContiguous);
        }
        let mut events = Vec::new();
        if let Some(active) = self.active.take() {
            events.push(SpeechEvent::Discarded {
                segment: active.info,
                reason: DiscardReason::Gap(gap.reason),
            });
        }
        self.identity = gap.new_identity;
        self.cursor = gap.range.end;
        self.eligible_start = gap.range.end;
        self.last_now_ns = now_ns;
        self.last_packet_ns = None;
        self.continuation = None;
        self.history.clear();
        self.closed = false;
        events.push(SpeechEvent::Gap(gap));
        self.reset_model(VadResetReason::Gap, true, &mut events);
        Ok(events)
    }
}

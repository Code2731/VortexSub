//! Platform-independent processing-thread audio primitives. No capture or inference.
mod normalize;
mod rolling;
mod vad;
pub use normalize::{AudioBatch, AudioFormat, AudioFrame, AudioTail, StreamNormalizer};
pub use rolling::{PcmSnapshot, RollingAudio, SnapshotPool};
pub use vad::{
    CaptureHealth, DiscardReason, EffectiveVadSettings, FinalReason, SegmentIdentity, SegmentInfo,
    SpeechEvent, VadResetReason, VadSegmenter, VadSettings,
};

pub const SAMPLE_RATE: u32 = 16_000;
pub const FRAME_SAMPLES: usize = 512;
pub const MAX_ROLLING_SAMPLES: usize = 12 * SAMPLE_RATE as usize;
pub const MAX_SNAPSHOT_SAMPLES: usize = 8 * SAMPLE_RATE as usize;
pub const MAX_SNAPSHOT_SLOTS: usize = 4;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AudioIdentity {
    pub session_id: u64,
    pub epoch: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JobIdentity {
    pub audio: AudioIdentity,
    pub segment_id: u64,
    pub source_revision: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SampleRange {
    pub start: u64,
    pub end: u64,
}
impl SampleRange {
    pub fn new(start: u64, end: u64) -> Result<Self, AudioError> {
        if start >= end {
            return Err(AudioError::InvalidRange);
        }
        Ok(Self { start, end })
    }
    pub fn start_s(self) -> f64 {
        self.start as f64 / SAMPLE_RATE as f64
    }
    pub fn end_s(self) -> f64 {
        self.end as f64 / SAMPLE_RATE as f64
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GapReason {
    Pause,
    DeviceChange,
    Discontinuity,
    Overload,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AudioGap {
    pub old_identity: AudioIdentity,
    pub new_identity: AudioIdentity,
    pub range: SampleRange,
    pub reason: GapReason,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AudioError {
    UnsupportedFormat,
    InvalidPacket,
    NonFiniteSample,
    InvalidAmplitude,
    ClosedStream,
    TimestampOverflow,
    InvalidRange,
    NonContiguous,
    StaleIdentity,
    RangeUnavailable,
    ResourceExhausted,
    InvalidCapacity,
    InvalidVadConfig,
    InvalidProbability,
    ClockRegression,
}
impl std::fmt::Display for AudioError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "audio core: {self:?}")
    }
}
impl std::error::Error for AudioError {}

/// Convert an adapter's monotonic session-relative nanoseconds once, at stream attach.
/// Native frame offsets are then mapped rationally, without rounding each packet.
pub fn session_sample_from_ns(ns: u64) -> Result<u64, AudioError> {
    u64::try_from(ns as u128 * SAMPLE_RATE as u128 / 1_000_000_000)
        .map_err(|_| AudioError::TimestampOverflow)
}

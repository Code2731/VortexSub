//! Serialized, reusable whisper.cpp context. Capture and scheduling stay outside this adapter.
mod cancellation;
pub use cancellation::{Cancellation, CancellationSnapshot};
#[cfg(feature = "native")]
mod native;
#[cfg(feature = "native")]
pub use native::{AsrEngine, DecodeOutcome};

/// Native segment times are relative to the supplied PCM, in milliseconds.
#[derive(Clone, Debug)]
pub struct Segment {
    pub start_ms: i64,
    pub end_ms: i64,
    pub text: String,
    pub tokens: Vec<Token>,
}

/// Byte offsets address the validated segment text; timestamps address input PCM.
#[derive(Clone, Debug)]
pub struct Token {
    pub byte_start: usize,
    pub byte_end: usize,
    pub start_ms: i64,
    pub end_ms: i64,
    /// Experimental DTW emission landmark, not a word start/end interval.
    pub dtw_ms: Option<i64>,
}

pub const ENGINE_ID: &str = "whisper-rs=0.14.4; whisper-rs-sys=0.13.1; bundled whisper.cpp=1.7.4";

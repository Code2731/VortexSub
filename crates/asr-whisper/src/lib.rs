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
}

pub const ENGINE_ID: &str = "whisper-rs=0.14.4; whisper-rs-sys=0.13.1; bundled whisper.cpp=1.7.4";

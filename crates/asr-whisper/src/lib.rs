//! Serialized, reusable whisper.cpp context. Capture and scheduling stay outside this adapter.
mod cancellation;
pub use cancellation::{Cancellation, CancellationSnapshot};
#[cfg(feature = "native")]
mod native;
#[cfg(feature = "native")]
pub use native::{AsrEngine, DecodeOutcome, Segment};

pub const ENGINE_ID: &str = "whisper-rs=0.14.4; whisper-rs-sys=0.13.1; bundled whisper.cpp=1.7.4";

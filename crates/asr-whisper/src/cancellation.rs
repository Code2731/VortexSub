use std::sync::{
    atomic::{AtomicBool, AtomicUsize, Ordering},
    Arc,
};

/// One job, one token. A cancelled/used token is never reset for a new decode.
#[derive(Clone, Default)]
pub struct Cancellation(pub(crate) Arc<Control>);

#[derive(Default)]
#[cfg_attr(not(feature = "native"), allow(dead_code))]
pub(crate) struct Control {
    pub requested: AtomicBool,
    pub claimed: AtomicBool,
    pub running: AtomicBool,
    pub checks: AtomicUsize,
    pub encoder_entries: AtomicUsize,
    pub observed: AtomicBool,
}

pub struct CancellationSnapshot {
    pub requested: bool,
    pub running: bool,
    pub native_checks: usize,
    pub encoder_entries: usize,
    pub abort_observed: bool,
}

impl Cancellation {
    pub fn request(&self) {
        self.0.requested.store(true, Ordering::Release);
    }

    pub fn snapshot(&self) -> CancellationSnapshot {
        CancellationSnapshot {
            requested: self.0.requested.load(Ordering::Acquire),
            running: self.0.running.load(Ordering::Acquire),
            native_checks: self.0.checks.load(Ordering::Acquire),
            encoder_entries: self.0.encoder_entries.load(Ordering::Acquire),
            abort_observed: self.0.observed.load(Ordering::Acquire),
        }
    }
}

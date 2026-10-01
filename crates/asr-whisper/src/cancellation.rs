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
    pub requested: Arc<AtomicBool>,
    pub claimed: AtomicBool,
    pub running: Arc<AtomicBool>,
    pub checks: Arc<AtomicUsize>,
    pub encoder_entries: Arc<AtomicUsize>,
    pub observed: Arc<AtomicBool>,
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fallback_attempt_shares_cancellation_and_reservation_but_not_claim() {
        let first = Cancellation::default();
        first.0.claimed.store(true, Ordering::Release);
        let next = first.next_attempt();
        assert!(!next.0.claimed.load(Ordering::Acquire));
        next.0.running.store(true, Ordering::Release);
        assert!(first.snapshot().running);
        first.request();
        assert!(next.snapshot().requested);
        next.0.observed.store(true, Ordering::Release);
        assert!(first.snapshot().abort_observed);
        next.0.running.store(false, Ordering::Release);
        assert!(!first.snapshot().running);
        assert!(first.0.claimed.load(Ordering::Acquire));
    }
}

pub struct CancellationSnapshot {
    pub requested: bool,
    pub running: bool,
    pub native_checks: usize,
    pub encoder_entries: usize,
    pub abort_observed: bool,
}

impl Cancellation {
    /// Fresh single-use attempt for the same serialized job; cancellation and
    /// reservation diagnostics remain shared. Never dispatch attempts concurrently.
    pub fn next_attempt(&self) -> Self {
        Self(Arc::new(Control {
            requested: self.0.requested.clone(),
            claimed: AtomicBool::new(false),
            running: self.0.running.clone(),
            checks: self.0.checks.clone(),
            encoder_entries: self.0.encoder_entries.clone(),
            observed: self.0.observed.clone(),
        }))
    }
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

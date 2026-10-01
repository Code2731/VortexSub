//! Bounded feedback policy on the processing thread. No callback or model work.
use echosub_audio_core::SegmentIdentity;

pub struct AdaptivePartial {
    identity: Option<SegmentIdentity>,
    last_end: u64,
    growth: u64,
    repeats: u8,
    text: String,
    stable: String,
    pub decision: &'static str,
}
impl Default for AdaptivePartial {
    fn default() -> Self {
        Self {
            identity: None,
            last_end: 0,
            growth: 8192,
            repeats: 0,
            text: String::new(),
            stable: String::new(),
            decision: "Initial",
        }
    }
}
impl AdaptivePartial {
    pub fn reset(&mut self) {
        *self = Self::default();
    }
    pub fn due(&self, id: SegmentIdentity, end: u64) -> bool {
        self.identity != Some(id) || end.saturating_sub(self.last_end) >= self.growth
    }
    pub fn admitted(&mut self, id: SegmentIdentity, end: u64) {
        if self.identity != Some(id) {
            self.reset();
            self.identity = Some(id);
        }
        self.last_end = end;
    }
    pub fn growth_s(&self) -> f64 {
        self.growth as f64 / 16000.
    }
    pub fn feedback(
        &mut self,
        id: SegmentIdentity,
        text: Option<&str>,
        stable: &str,
        decode_s: f64,
    ) {
        if self.identity != Some(id) {
            return;
        }
        let target = match text.filter(|text| !text.is_empty()) {
            None => {
                self.repeats = 0;
                self.decision = "EmptyBackoff";
                8192
            }
            Some(text) if text == self.text && stable == self.stable => {
                self.repeats = self.repeats.saturating_add(1);
                self.decision = "UnchangedBackoff";
                (8192 + u64::from(self.repeats) * 4096).min(16384)
            }
            Some(text) => {
                self.repeats = 0;
                let confirming = stable.is_empty() || stable == self.stable;
                self.text.clear();
                self.text.push_str(text);
                self.stable.clear();
                self.stable.push_str(stable);
                self.decision = if confirming {
                    "ConfirmSoon"
                } else {
                    "StableProgress"
                };
                if confirming {
                    4096
                } else {
                    8192
                }
            }
        };
        let cost = if decode_s.is_finite() && decode_s > 0. {
            ((decode_s * 1.25 * 16000. / 512.).ceil() as u64).min(32) * 512
        } else {
            0
        };
        self.growth = target.max(cost);
        if cost > target {
            self.decision = "DecodeCostBackoff";
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    const ID: SegmentIdentity = SegmentIdentity {
        audio: echosub_audio_core::AudioIdentity {
            session_id: 1,
            epoch: 1,
        },
        segment_id: 1,
    };
    #[test]
    fn confirmation_empty_and_unchanged_feedback_choose_bounded_fresh_audio() {
        let mut policy = AdaptivePartial::default();
        policy.admitted(ID, 16000);
        policy.feedback(ID, Some("take the left"), "", 0.05);
        assert_eq!(policy.decision, "ConfirmSoon");
        assert!(!policy.due(ID, 16000 + 4095));
        assert!(policy.due(ID, 16000 + 4096));
        policy.feedback(ID, None, "", 0.05);
        assert_eq!(policy.growth_s(), 0.512);
        assert_eq!(policy.decision, "EmptyBackoff");
        policy.feedback(ID, Some("take the left path"), "take the left", 0.05);
        assert_eq!(policy.decision, "StableProgress");
        for _ in 0..10 {
            policy.feedback(ID, Some("take the left path"), "take the left", 0.05);
        }
        assert_eq!(policy.growth_s(), 1.024);
        assert_eq!(policy.decision, "UnchangedBackoff");
    }
    #[test]
    fn decode_cost_and_identity_do_not_create_extra_or_stale_work() {
        let mut policy = AdaptivePartial::default();
        policy.admitted(ID, 16000);
        policy.feedback(ID, Some("take the left"), "", 0.8);
        assert_eq!(policy.growth_s(), 1.024);
        assert_eq!(policy.decision, "DecodeCostBackoff");
        let next = SegmentIdentity {
            segment_id: 2,
            ..ID
        };
        assert!(policy.due(next, 16000));
        policy.feedback(next, None, "", 0.01);
        assert_eq!(policy.decision, "DecodeCostBackoff");
        policy.admitted(next, 16000);
        assert_eq!(policy.decision, "Initial");
        policy.reset();
        assert!(policy.due(ID, 1));
    }
}

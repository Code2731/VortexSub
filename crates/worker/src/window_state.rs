//! Processing-thread mapping: only applied partial revisions can authorize a cut.
use crate::decode_window::{prefix_dtw, window, Prefix};
use echosub_asr_whisper::Segment;
use echosub_audio_core::{JobIdentity, SampleRange};

#[derive(Clone)]
pub struct Plan {
    #[cfg_attr(not(feature = "native-asr"), allow(dead_code))]
    pub prefix: Prefix,
    pub start: u64,
}

#[cfg(test)]
mod tests {
    use super::*;
    use echosub_asr_whisper::Token;
    fn key(revision: u64) -> JobIdentity {
        JobIdentity {
            audio: echosub_audio_core::AudioIdentity {
                session_id: 1,
                epoch: 1,
            },
            segment_id: 1,
            source_revision: revision,
        }
    }
    fn segments() -> Vec<Segment> {
        vec![Segment {
            start_ms: 0,
            end_ms: 4000,
            text: " Go left now. Wait".into(),
            tokens: vec![
                Token {
                    byte_start: 0,
                    byte_end: 3,
                    start_ms: 0,
                    end_ms: 0,
                    dtw_ms: Some(200),
                },
                Token {
                    byte_start: 3,
                    byte_end: 8,
                    start_ms: 0,
                    end_ms: 0,
                    dtw_ms: Some(1800),
                },
                Token {
                    byte_start: 8,
                    byte_end: 13,
                    start_ms: 0,
                    end_ms: 0,
                    dtw_ms: Some(2300),
                },
                Token {
                    byte_start: 13,
                    byte_end: 18,
                    start_ms: 0,
                    end_ms: 0,
                    dtw_ms: Some(2800),
                },
            ],
        }]
    }
    #[test]
    fn mapping_requires_adjacent_applied_revisions_and_resets_after_trim_or_epoch() {
        let range = SampleRange {
            start: 0,
            end: 64000,
        };
        let mut state = WindowState::default();
        state.observe(key(1), range, "", Some(segments()));
        assert!(state.for_job(key(2), range).is_none());
        state.observe(key(2), range, "Go left now.", Some(segments()));
        assert!(state.for_job(key(3), range).is_some());
        assert!(state.for_job(key(4), range).is_none());
        let mut next = key(3);
        next.audio.epoch = 2;
        assert!(state.for_job(next, range).is_none());
        next = key(3);
        next.segment_id = 2;
        assert!(state.for_job(next, range).is_none());
        state.observe(key(3), range, "Go left now.", None);
        assert!(state.for_job(key(4), range).is_none());
        state.observe(key(4), range, "", Some(segments()));
        state.observe(key(5), range, "Go left now.", Some(segments()));
        assert!(state.for_job(key(6), range).is_some());
        state.clear();
        assert!(state.for_job(key(6), range).is_none());
    }
}
struct Observation {
    key: JobIdentity,
    range: SampleRange,
    segments: Vec<Segment>,
}
#[derive(Default)]
pub struct WindowState {
    previous: Option<Observation>,
    plan: Option<(JobIdentity, Plan)>,
    pub attempts: u64,
    pub fallbacks: u64,
}
impl WindowState {
    pub fn clear(&mut self) {
        self.previous = None;
        self.plan = None;
    }
    pub fn observe(
        &mut self,
        key: JobIdentity,
        range: SampleRange,
        stable: &str,
        segments: Option<Vec<Segment>>,
    ) {
        self.plan = None;
        let Some(segments) = segments.filter(|s| {
            s.len() <= 4096
                && s.iter().map(|s| s.text.len()).sum::<usize>() <= 4096
                && s.iter().map(|s| s.tokens.len()).sum::<usize>() <= 4096
        }) else {
            self.clear();
            return;
        };
        if let Some(old) = self.previous.as_ref().filter(|p| {
            p.key.audio == key.audio
                && p.key.segment_id == key.segment_id
                && p.key.source_revision.checked_add(1) == Some(key.source_revision)
                && p.range.start == range.start
                && p.range.end <= range.end
        }) {
            if let (Ok(a), Ok(b)) = (
                prefix_dtw(&old.segments, old.range, stable),
                prefix_dtw(&segments, range, stable),
            ) {
                if let Some(w) = window(&a, &b, range) {
                    self.plan = Some((
                        key,
                        Plan {
                            prefix: b,
                            start: w.start,
                        },
                    ));
                }
            }
        }
        self.previous = Some(Observation {
            key,
            range,
            segments,
        });
    }
    pub fn for_job(&self, key: JobIdentity, range: SampleRange) -> Option<Plan> {
        let (old, plan) = self.plan.as_ref()?;
        let observation = self.previous.as_ref()?;
        (old.audio == key.audio
            && old.segment_id == key.segment_id
            && old.source_revision.checked_add(1) == Some(key.source_revision)
            && range.start == observation.range.start
            && range.end >= observation.range.end
            && range.end.saturating_sub(plan.start) >= 16000)
            .then(|| plan.clone())
    }
}

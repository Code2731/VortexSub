//! Conservative timestamp and exact-text reconciliation on the serialized ASR owner.
use echosub_asr_whisper::Segment;
use echosub_audio_core::{SampleRange, SegmentIdentity};
use echosub_pipeline_core::{AsrKind, Outcome, MAX_TEXT_BYTES};

struct Previous {
    id: SegmentIdentity,
    range: SampleRange,
    segments: Vec<Segment>,
}
#[derive(Default)]
pub struct Reconciler {
    previous: Option<Previous>,
    pub tokens_removed: usize,
    pub timed_tokens: usize,
}
fn text(segments: &[Segment]) -> Option<String> {
    let mut text = String::new();
    for segment in segments {
        if segment.text.contains('\0') {
            return None;
        }
        if text.len().checked_add(segment.text.len())? > MAX_TEXT_BYTES {
            return None;
        }
        text.push_str(&segment.text);
    }
    Some(text.trim().to_owned())
}
fn absolute(segment: &Segment, range: SampleRange) -> Option<SampleRange> {
    let start = u64::try_from(segment.start_ms)
        .ok()?
        .checked_mul(16)?
        .checked_add(range.start)?;
    let end = u64::try_from(segment.end_ms)
        .ok()?
        .checked_mul(16)?
        .checked_add(range.start)?;
    (start < end && end <= range.end).then_some(SampleRange { start, end })
}
fn ordered(segments: &[Segment], range: SampleRange) -> bool {
    let mut previous_end = range.start;
    for segment in segments {
        let Some(span) = absolute(segment, range) else {
            return false;
        };
        if span.start < previous_end {
            return false;
        }
        previous_end = span.end;
    }
    true
}
impl Reconciler {
    pub fn finish(
        &mut self,
        id: SegmentIdentity,
        range: SampleRange,
        kind: AsrKind,
        continued_from: Option<SegmentIdentity>,
        segments: Vec<Segment>,
    ) -> (Outcome, usize) {
        self.tokens_removed = 0;
        self.timed_tokens = segments
            .iter()
            .flat_map(|s| &s.tokens)
            .filter(|t| {
                t.start_ms >= 0
                    && t.end_ms > t.start_ms
                    && (t.end_ms as u64)
                        .checked_mul(16)
                        .is_some_and(|end| end <= range.end - range.start)
            })
            .count();
        // Bound native metadata as well as the visible UTF-8 text.
        if segments.len() > MAX_TEXT_BYTES {
            return (Outcome::Failed, 0);
        }
        let Some(raw) = text(&segments) else {
            return (Outcome::Failed, 0);
        };
        let mut removed = 0;
        if let Some(previous) = self.previous.as_ref().filter(|p| {
            Some(p.id) == continued_from
                && p.id.audio == id.audio
                && p.id.segment_id < id.segment_id
                && p.range.start < range.start
                && range.start < p.range.end
                && p.range.end < range.end
                // 0.6 s rounded to 19 x 512-sample VAD frames = 0.608 s.
                && p.range.end - range.start <= 9728
                && ordered(&p.segments, p.range)
                && ordered(&segments, range)
        }) {
            let overlap = SampleRange {
                start: range.start,
                end: previous.range.end,
            };
            // Remove whole native segments only. A segment crossing the boundary is ambiguous.
            let prefix_count = segments
                .iter()
                .take_while(|s| {
                    absolute(s, range)
                        .is_some_and(|r| r.start >= overlap.start && r.end <= overlap.end)
                })
                .count();
            let tail_start = previous
                .segments
                .iter()
                .rposition(|s| {
                    !absolute(s, previous.range)
                        .is_some_and(|r| r.start >= overlap.start && r.end <= overlap.end)
                })
                .map_or(0, |i| i + 1);
            let tail = &previous.segments[tail_start..];
            for count in 1..=prefix_count {
                let candidate = text(&segments[..count]).unwrap();
                if candidate.is_empty() {
                    continue;
                }
                // Compare complete suffix spans, preserving all characters in retained spans.
                if (0..tail.len()).any(|start| text(&tail[start..]).is_some_and(|t| t == candidate))
                {
                    removed = count;
                }
            }
        }
        let mut output = text(&segments[removed..]).unwrap();
        if let Some(previous) = self.previous.as_ref().filter(|p| {
            Some(p.id) == continued_from
                && p.id.audio == id.audio
                && p.id.segment_id < id.segment_id
                && p.range.start < range.start
                && range.start < p.range.end
                && p.range.end < range.end
                && p.range.end - range.start <= 9728
        }) {
            if let Some((suffix, count)) = super::token_reconcile::reconcile(
                &previous.segments,
                previous.range,
                &segments,
                range,
            ) {
                // Fine alignment must remove at least as much as complete-span alignment.
                if suffix.len() < output.len() {
                    output = suffix;
                    self.tokens_removed = count;
                }
            }
        }
        if kind == AsrKind::Final {
            self.previous = (!raw.is_empty()).then_some(Previous {
                id,
                range,
                segments,
            });
        }
        let outcome = if raw.is_empty() {
            Outcome::NoSpeech
        } else if output.is_empty() && (removed > 0 || self.tokens_removed > 0) {
            Outcome::OverlapOnly
        } else {
            Outcome::Text(output)
        };
        (outcome, removed)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use echosub_audio_core::AudioIdentity;
    fn id(segment_id: u64) -> SegmentIdentity {
        SegmentIdentity {
            audio: AudioIdentity {
                session_id: 1,
                epoch: 1,
            },
            segment_id,
        }
    }
    fn span(start_ms: i64, end_ms: i64, text: &str) -> Segment {
        Segment {
            start_ms,
            end_ms,
            text: text.into(),
            tokens: Vec::new(),
        }
    }
    fn previous(r: &mut Reconciler) {
        r.finish(
            id(1),
            SampleRange {
                start: 0,
                end: 128000,
            },
            AsrKind::Final,
            None,
            vec![span(0, 7400, "앞 문장 "), span(7400, 8000, "안 돼")],
        );
    }
    #[test]
    fn overlap_requires_continuation_time_and_exact_suffix_but_keeps_real_repetition() {
        let mut r = Reconciler::default();
        previous(&mut r);
        let (outcome, n) = r.finish(
            id(2),
            SampleRange {
                start: 118400,
                end: 246400,
            },
            AsrKind::Final,
            Some(id(1)),
            vec![span(0, 600, "안 돼"), span(600, 1300, "안 돼, 안 돼")],
        );
        assert_eq!(n, 1);
        assert!(matches!(outcome, Outcome::Text(t) if t == "안 돼, 안 돼"));
    }
    #[test]
    fn ambiguous_boundary_unlinked_epoch_and_text_mismatch_are_preserved() {
        for (parent, end, text) in [
            (None, 600, "안 돼"),
            (Some(id(1)), 700, "안 돼"),
            (Some(id(1)), 600, "안돼"),
        ] {
            let mut r = Reconciler::default();
            previous(&mut r);
            let (outcome, n) = r.finish(
                id(2),
                SampleRange {
                    start: 118400,
                    end: 246400,
                },
                AsrKind::Final,
                parent,
                vec![span(0, end, text)],
            );
            assert_eq!(n, 0);
            assert!(matches!(outcome, Outcome::Text(t) if t == text));
        }
        let mut r = Reconciler::default();
        previous(&mut r);
        let mut next = id(2);
        next.audio.epoch += 1;
        assert_eq!(
            r.finish(
                next,
                SampleRange {
                    start: 118400,
                    end: 246400
                },
                AsrKind::Final,
                Some(id(1)),
                vec![span(0, 600, "안 돼")]
            )
            .1,
            0
        );
    }
    #[test]
    fn japanese_unicode_and_partial_updates_do_not_replace_previous_final_context() {
        let mut r = Reconciler::default();
        r.finish(
            id(1),
            SampleRange {
                start: 0,
                end: 128000,
            },
            AsrKind::Final,
            None,
            vec![span(0, 7400, "前の台詞"), span(7400, 8000, "進もう🙂")],
        );
        for kind in [AsrKind::Partial, AsrKind::Partial, AsrKind::Final] {
            let (outcome, removed) = r.finish(
                id(2),
                SampleRange {
                    start: 118400,
                    end: 246400,
                },
                kind,
                Some(id(1)),
                vec![span(0, 600, "進もう🙂"), span(600, 1200, "進もう🙂")],
            );
            assert_eq!(removed, 1);
            assert!(matches!(outcome, Outcome::Text(t) if t == "進もう🙂"));
        }
    }
    #[test]
    fn token_fallback_respects_product_identity_and_resets_diagnostics() {
        use echosub_asr_whisper::Token;
        let mut old = span(0, 8000, "We should go");
        old.tokens = vec![
            Token {
                byte_start: 0,
                byte_end: 9,
                start_ms: 0,
                end_ms: 7000,
                dtw_ms: None,
            },
            Token {
                byte_start: 9,
                byte_end: 12,
                start_ms: 7400,
                end_ms: 8000,
                dtw_ms: None,
            },
        ];
        let mut next = span(0, 8000, " go go again");
        next.tokens = vec![
            Token {
                byte_start: 0,
                byte_end: 3,
                start_ms: 10,
                end_ms: 600,
                dtw_ms: None,
            },
            Token {
                byte_start: 3,
                byte_end: 6,
                start_ms: 610,
                end_ms: 900,
                dtw_ms: None,
            },
            Token {
                byte_start: 6,
                byte_end: 12,
                start_ms: 900,
                end_ms: 1500,
                dtw_ms: None,
            },
        ];
        for parent in [None, Some(id(1))] {
            let mut r = Reconciler::default();
            r.finish(
                id(1),
                SampleRange {
                    start: 0,
                    end: 128000,
                },
                AsrKind::Final,
                None,
                vec![old.clone()],
            );
            let (outcome, spans) = r.finish(
                id(2),
                SampleRange {
                    start: 118272,
                    end: 246272,
                },
                AsrKind::Partial,
                parent,
                vec![next.clone()],
            );
            assert_eq!(spans, 0);
            assert_eq!(r.timed_tokens, 3);
            if parent.is_some() {
                assert_eq!(r.tokens_removed, 1);
                assert!(matches!(outcome, Outcome::Text(t) if t == "go again"));
            } else {
                assert_eq!(r.tokens_removed, 0);
                assert!(matches!(outcome, Outcome::Text(t) if t == "go go again"));
            }
            r.finish(
                id(2),
                SampleRange {
                    start: 118272,
                    end: 246272,
                },
                AsrKind::Final,
                parent,
                vec![],
            );
            assert_eq!(r.tokens_removed, 0);
            assert_eq!(r.timed_tokens, 0);
        }
    }
    #[test]
    fn empty_duplicate_only_invalid_time_and_oversized_native_output_are_distinct() {
        let mut r = Reconciler::default();
        previous(&mut r);
        assert!(matches!(
            r.finish(
                id(2),
                SampleRange {
                    start: 118400,
                    end: 246400
                },
                AsrKind::Final,
                Some(id(1)),
                vec![span(0, 600, "안 돼")]
            )
            .0,
            Outcome::OverlapOnly
        ));
        assert!(matches!(
            r.finish(
                id(3),
                SampleRange {
                    start: 236800,
                    end: 364800
                },
                AsrKind::Final,
                Some(id(2)),
                vec![]
            )
            .0,
            Outcome::NoSpeech
        ));
        assert!(matches!(
            r.finish(
                id(4),
                SampleRange {
                    start: 0,
                    end: 16000
                },
                AsrKind::Final,
                None,
                vec![span(-1, 500, "実際の声")]
            )
            .0,
            Outcome::Text(_)
        ));
        assert!(matches!(
            r.finish(
                id(5),
                SampleRange {
                    start: 0,
                    end: 16000
                },
                AsrKind::Final,
                None,
                vec![span(0, 500, &"x".repeat(4097))]
            )
            .0,
            Outcome::Failed
        ));
    }
}

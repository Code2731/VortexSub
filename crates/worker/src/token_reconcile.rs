//! Exact token text matching. Cuts require validated UTF-8 and ASCII word boundaries.
use echosub_asr_whisper::{Segment, Token};
use echosub_audio_core::SampleRange;
use std::collections::HashSet;

struct Timed {
    start: usize,
    end: usize,
    audio: Option<SampleRange>,
}
fn absolute(token: &Token, range: SampleRange, punctuation: bool) -> Option<SampleRange> {
    let start = range
        .start
        .checked_add(u64::try_from(token.start_ms).ok()?.checked_mul(16)?)?;
    let end = range
        .start
        .checked_add(u64::try_from(token.end_ms).ok()?.checked_mul(16)?)?;
    ((start < end || (punctuation && start == end)) && end <= range.end)
        .then_some(SampleRange { start, end })
}
fn flatten(segments: &[Segment], range: SampleRange) -> Option<(String, Vec<Timed>)> {
    let mut text = String::new();
    let mut tokens = Vec::new();
    for segment in segments {
        if segment.tokens.is_empty() || text.len() + segment.text.len() > 4096 {
            return None;
        }
        let offset = text.len();
        let mut expected = 0;
        for token in &segment.tokens {
            if tokens.len() == 4096
                || token.byte_start != expected
                || token.byte_start >= token.byte_end
                || token.byte_end > segment.text.len()
            {
                return None;
            }
            tokens.push(Timed {
                start: offset + token.byte_start,
                end: offset + token.byte_end,
                audio: absolute(
                    token,
                    range,
                    segment.text.as_bytes()[token.byte_start..token.byte_end]
                        .iter()
                        .all(|b| b.is_ascii_punctuation() || b.is_ascii_whitespace()),
                ),
            });
            expected = token.byte_end;
        }
        if expected != segment.text.len() {
            return None;
        }
        text.push_str(&segment.text);
    }
    Some((text, tokens))
}
fn boundary(text: &str, offset: usize) -> bool {
    if !text.is_char_boundary(offset) {
        return false;
    }
    if offset == 0 || offset == text.len() {
        return true;
    }
    // Do not split combining marks, ZWJ emoji or unspaced CJK text.
    text.as_bytes()[offset].is_ascii()
        && (text.as_bytes()[offset].is_ascii_whitespace()
            || text.as_bytes()[offset - 1].is_ascii_whitespace())
}
pub fn reconcile(
    previous: &[Segment],
    previous_range: SampleRange,
    current: &[Segment],
    current_range: SampleRange,
) -> Option<(String, usize)> {
    let (old, old_tokens) = flatten(previous, previous_range)?;
    let (new, new_tokens) = flatten(current, current_range)?;
    let overlap = SampleRange {
        start: current_range.start,
        end: previous_range.end,
    };
    // Validate each suffix once; avoid rescanning token ranges per text candidate.
    let mut suffixes = HashSet::new();
    let mut next_start = overlap.end;
    for token in old_tokens.iter().rev() {
        let Some(audio) = token.audio else {
            break;
        };
        if audio.start < overlap.start || audio.end > next_start {
            break;
        }
        next_start = audio.start;
        if boundary(&old, token.start) {
            suffixes.insert(old[token.start..].trim());
        }
    }
    let mut result = None;
    let mut end = overlap.start;
    for (i, token) in new_tokens.iter().enumerate() {
        let Some(audio) = token.audio else {
            break;
        };
        if audio.start < end || audio.start < overlap.start || audio.end > overlap.end {
            break;
        }
        end = audio.end;
        if !boundary(&new, token.end) {
            continue;
        }
        let candidate = new[..token.end].trim();
        if candidate.is_empty() {
            continue;
        }
        if suffixes.contains(candidate) {
            result = Some((new[token.end..].trim().to_owned(), i + 1));
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    fn segment(text: &str, pieces: &[(&str, i64, i64)]) -> Segment {
        let mut offset = 0;
        let tokens = pieces
            .iter()
            .map(|(piece, start, end)| {
                let token = Token {
                    byte_start: offset,
                    byte_end: offset + piece.len(),
                    start_ms: *start,
                    end_ms: *end,
                    dtw_ms: None,
                };
                offset = token.byte_end;
                token
            })
            .collect();
        Segment {
            start_ms: 0,
            end_ms: 8000,
            text: text.into(),
            tokens,
        }
    }
    const OLD: SampleRange = SampleRange {
        start: 0,
        end: 128000,
    };
    const NEW: SampleRange = SampleRange {
        start: 118272,
        end: 246272,
    };
    #[test]
    fn coarse_sentence_boundary_can_remove_timed_words_and_preserve_real_repetition() {
        let old = segment(
            "We should go",
            &[("We should", 0, 7000), (" go", 7400, 8000)],
        );
        let new = segment(
            " go go again",
            &[(" go", 10, 600), (" go", 610, 900), (" again", 900, 1500)],
        );
        assert_eq!(
            reconcile(&[old], OLD, &[new], NEW),
            Some(("go again".into(), 1))
        );
    }
    #[test]
    fn split_utf8_is_reassembled_but_unsafe_unspaced_and_combining_cuts_are_preserved() {
        let old = segment("앞 안 돼", &[("앞", 0, 7000), (" 안 돼", 7400, 8000)]);
        let mut new = segment(" 안 돼 다음", &[(" 안 돼", 10, 600), (" 다음", 610, 1000)]);
        let first = new.tokens.remove(0);
        new.tokens.insert(
            0,
            Token {
                byte_start: 0,
                byte_end: 2,
                start_ms: 10,
                end_ms: 100,
                dtw_ms: None,
            },
        );
        new.tokens.insert(
            1,
            Token {
                byte_start: 2,
                byte_end: first.byte_end,
                start_ms: 100,
                end_ms: 600,
                dtw_ms: None,
            },
        );
        assert_eq!(
            reconcile(&[old], OLD, &[new], NEW),
            Some(("다음".into(), 2))
        );
        for (old_text, new_text, prefix, suffix) in [
            ("進もう", "進もう進もう", "進もう", "進もう"),
            ("e", "e\u{301}", "e", "\u{301}"),
        ] {
            let old = segment(old_text, &[(old_text, 7400, 8000)]);
            let new = segment(new_text, &[(prefix, 10, 600), (suffix, 610, 1000)]);
            assert!(reconcile(&[old], OLD, &[new], NEW).is_none());
        }
    }
    #[test]
    fn punctuation_points_are_allowed_but_untimed_words_are_not() {
        let old = segment(" go.", &[(" go", 7400, 8000), (".", 8000, 8000)]);
        let new = segment(
            " go. go.",
            &[(" go", 10, 600), (".", 600, 600), (" go.", 610, 1000)],
        );
        assert_eq!(reconcile(&[old], OLD, &[new], NEW), Some(("go.".into(), 2)));
    }
    #[test]
    fn unknown_times_crossing_overlap_subwords_and_bad_offsets_do_not_trim() {
        for end in [0, 610, 900] {
            let old = segment(" go", &[(" go", 7400, 8000)]);
            let new = segment(" go next", &[(" go", 0, end), (" next", 1000, 1400)]);
            assert!(reconcile(&[old], OLD, &[new], NEW).is_none());
        }
        let old = segment("cargo", &[("car", 0, 7400), ("go", 7400, 8000)]);
        let new = segment("go next", &[("go", 10, 600), (" next", 610, 1000)]);
        assert!(reconcile(&[old], OLD, &[new], NEW).is_none());
        let old = segment("go", &[("go", 7400, 8000)]);
        let mut new = segment("go next", &[("go", 10, 600), (" next", 610, 1000)]);
        new.tokens[0].byte_end = 999;
        assert!(reconcile(&[old], OLD, &[new], NEW).is_none());
    }
}

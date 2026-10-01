//! Fail-closed alignment and suffix reconstruction. File fixtures only for now.
use echosub_asr_whisper::Segment;
use echosub_audio_core::SampleRange;

const MAX_BYTES: usize = 4096;
const TOLERANCE: u64 = 2560; // 0.16 seconds
const CONTEXT: u64 = 9728; // 0.608 seconds, 19 VAD frames

#[derive(Debug, PartialEq, Eq)]
pub struct Prefix {
    pub text: String,
    pub end: u64,
    anchor: String,
    anchor_start: u64,
    dtw: bool,
}

/// Keep native byte offsets until all text and absolute sample bounds are checked.
pub fn prefix(
    segments: &[Segment],
    range: SampleRange,
    stable: &str,
) -> Result<Prefix, &'static str> {
    if range.start >= range.end || stable.is_empty() || stable.len() > 1024 {
        return Err("InvalidPrefixOrRange");
    }
    let mut raw = String::new();
    for segment in segments {
        if raw.len().saturating_add(segment.text.len()) > MAX_BYTES || segment.text.contains('\0') {
            return Err("InvalidText");
        }
        raw.push_str(&segment.text);
    }
    if !raw.trim_start().starts_with(stable) {
        return Err("PrefixMismatch");
    }
    let target = raw.len() - raw.trim_start().len() + stable.len();
    // A text prefix cannot stop inside an ASCII word or an unspaced Unicode word.
    if target < raw.len() && !raw.as_bytes()[target].is_ascii_whitespace() {
        return Err("UnsafeWordBoundary");
    }
    let mut base = 0;
    let mut previous_end = range.start;
    let mut spans = Vec::new();
    for segment in segments {
        let mut covered = 0;
        for token in &segment.tokens {
            if spans.len() == MAX_BYTES
                || token.byte_start != covered
                || token.byte_end <= covered
                || token.byte_end > segment.text.len()
            {
                return Err("IncompleteTokenCoverage");
            }
            let start_byte = base + token.byte_start;
            let end_byte = base + token.byte_end;
            if end_byte > target {
                return Err("CrossingPrefixToken");
            }
            let piece = raw.get(start_byte..end_byte).ok_or("UnsafeUtf8Boundary")?;
            let punctuation = piece
                .chars()
                .all(|c| c.is_ascii_punctuation() || c.is_whitespace());
            let absolute = |ms: i64| {
                u64::try_from(ms)
                    .ok()?
                    .checked_mul(16)?
                    .checked_add(range.start)
            };
            let start = absolute(token.start_ms).ok_or("InvalidTokenTime")?;
            let end = absolute(token.end_ms).ok_or("InvalidTokenTime")?;
            if start < previous_end
                || end < start
                || end > range.end
                || (start == end && !punctuation)
            {
                return Err("InvalidOrZeroLengthWordTime");
            }
            spans.push((start_byte, end_byte, start, end));
            covered = token.byte_end;
            previous_end = end;
            if end_byte == target {
                if end <= range.start || end >= range.end {
                    return Err("BoundaryOutsidePcm");
                }
                let anchor = spans
                    .iter()
                    .find(|(byte, _, start, _)| {
                        *start >= end.saturating_sub(CONTEXT)
                            && (*byte == 0 || raw.as_bytes()[*byte].is_ascii_whitespace())
                            && raw[*byte..target].split_whitespace().count() >= 2
                    })
                    .ok_or("InsufficientOverlapAnchor")?;
                return Ok(Prefix {
                    text: stable.to_owned(),
                    end,
                    anchor: raw[anchor.0..target].trim().to_owned(),
                    anchor_start: anchor.2,
                    dtw: false,
                });
            }
        }
        if covered != segment.text.len() {
            return Err("IncompleteTokenCoverage");
        }
        base += segment.text.len();
    }
    Err("MissingTokenCoverage")
}

/// DTW landmarks remain points. They never repair or replace t0/t1 intervals.
/// A cut is only a file candidate; exact overlap reconstruction must still succeed.
pub fn prefix_dtw(
    segments: &[Segment],
    range: SampleRange,
    stable: &str,
) -> Result<Prefix, &'static str> {
    if range.start >= range.end || stable.is_empty() || stable.len() > 1024 {
        return Err("InvalidPrefixOrRange");
    }
    let mut raw = String::new();
    for segment in segments {
        if raw.len().saturating_add(segment.text.len()) > MAX_BYTES || segment.text.contains('\0') {
            return Err("InvalidText");
        }
        raw.push_str(&segment.text);
    }
    if !raw.trim_start().starts_with(stable) {
        return Err("PrefixMismatch");
    }
    let target = raw.len() - raw.trim_start().len() + stable.len();
    if target < raw.len() && !raw.as_bytes()[target].is_ascii_whitespace() {
        return Err("UnsafeWordBoundary");
    }
    let mut base = 0;
    let mut previous = range.start;
    let mut lexical = Vec::new();
    for segment in segments {
        let mut covered = 0;
        for token in &segment.tokens {
            if token.byte_start != covered
                || token.byte_end <= covered
                || token.byte_end > segment.text.len()
            {
                return Err("IncompleteTokenCoverage");
            }
            let byte = base + token.byte_start;
            let end = base + token.byte_end;
            if end > target {
                return Err("CrossingPrefixToken");
            }
            let piece = raw.get(byte..end).ok_or("UnsafeUtf8Boundary")?;
            let point = token
                .dtw_ms
                .and_then(|ms| u64::try_from(ms).ok())
                .and_then(|ms| ms.checked_mul(16))
                .and_then(|s| range.start.checked_add(s))
                .ok_or("MissingDtwLandmark")?;
            if point < previous || point >= range.end {
                return Err("InvalidDtwLandmark");
            }
            previous = point;
            if piece
                .chars()
                .any(|c| !c.is_whitespace() && !c.is_ascii_punctuation())
            {
                if lexical.len() == MAX_BYTES {
                    return Err("InvalidText");
                }
                lexical.push((byte, point));
            }
            covered = token.byte_end;
            if end == target {
                let last = lexical.last().ok_or("MissingDtwLandmark")?.1;
                let anchor = lexical
                    .iter()
                    .find(|(byte, point)| {
                        *point >= last.saturating_sub(CONTEXT)
                            && (*byte == 0 || raw.as_bytes()[*byte].is_ascii_whitespace())
                            && raw[*byte..target].split_whitespace().take(2).count() == 2
                    })
                    .ok_or("InsufficientOverlapAnchor")?;
                return Ok(Prefix {
                    text: stable.into(),
                    end: last,
                    anchor: raw[anchor.0..target].trim().into(),
                    anchor_start: anchor.1,
                    dtw: true,
                });
            }
        }
        if covered != segment.text.len() {
            return Err("IncompleteTokenCoverage");
        }
        base += segment.text.len();
    }
    Err("MissingTokenCoverage")
}

pub fn window(first: &Prefix, second: &Prefix, product: SampleRange) -> Option<SampleRange> {
    if first.dtw != second.dtw
        || first.text != second.text
        || first.end.abs_diff(second.end) > TOLERANCE
        || first.end >= product.end
        || second.end >= product.end
    {
        return None;
    }
    let start = first.end.min(second.end).checked_sub(CONTEXT)? / 512 * 512;
    (start > product.start
        && product.end.saturating_sub(start) >= 16000
        && first.anchor_start >= start
        && second.anchor_start >= start)
        .then_some(SampleRange {
            start,
            end: product.end,
        })
}

/// Exact timed anchor must be the start of the new decode; never fuzzy-delete words.
pub fn merge(
    old: &Prefix,
    segments: &[Segment],
    window: SampleRange,
) -> Result<String, &'static str> {
    let anchor = if old.dtw {
        prefix_dtw(segments, window, &old.anchor)
    } else {
        prefix(segments, window, &old.anchor)
    }?;
    if anchor.end.abs_diff(old.end) > TOLERANCE
        || anchor.anchor_start.abs_diff(old.anchor_start) > TOLERANCE
    {
        return Err("OverlapTimeMismatch");
    }
    let mut raw = String::new();
    for segment in segments {
        if raw.len().saturating_add(segment.text.len()) > MAX_BYTES || segment.text.contains('\0') {
            return Err("InvalidText");
        }
        raw.push_str(&segment.text);
    }
    let tail = &raw.trim_start()[old.anchor.len()..];
    let mut joined = old.text.clone();
    joined.push_str(tail);
    if joined.len() > MAX_BYTES {
        return Err("InvalidText");
    }
    Ok(joined.trim().to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use echosub_asr_whisper::Token;
    fn segment(text: &str, pieces: &[(&str, i64, i64)]) -> Segment {
        let mut byte = 0;
        Segment {
            start_ms: 0,
            end_ms: 4000,
            text: text.into(),
            tokens: pieces
                .iter()
                .map(|(s, a, b)| {
                    let token = Token {
                        byte_start: byte,
                        byte_end: byte + s.len(),
                        start_ms: *a,
                        end_ms: *b,
                        dtw_ms: None,
                    };
                    byte += s.len();
                    token
                })
                .collect(),
        }
    }
    #[test]
    fn dtw_points_are_separate_from_invalid_intervals_and_require_valid_landmarks() {
        let range = SampleRange {
            start: 0,
            end: 64000,
        };
        let mut native = segment(
            " Go left now. Wait",
            &[
                (" Go", 0, 0),
                (" left", 0, 0),
                (" now.", 0, 0),
                (" Wait", 0, 0),
            ],
        );
        for (token, ms) in native.tokens.iter_mut().zip([200, 1800, 2300, 2800]) {
            token.dtw_ms = Some(ms);
        }
        assert!(prefix(&[native.clone()], range, "Go left now.").is_err());
        let p = prefix_dtw(&[native.clone()], range, "Go left now.").unwrap();
        assert_eq!(p.end, 36800);
        let w = window(&p, &p, range).unwrap();
        let mut tail = segment(
            " left now. Wait left now.",
            &[(" left", 0, 0), (" now.", 0, 0), (" Wait left now.", 0, 0)],
        );
        for (token, ms) in tail.tokens.iter_mut().zip([1800, 2300, 2800]) {
            token.dtw_ms = Some(ms - w.start as i64 / 16);
        }
        assert_eq!(
            merge(&p, &[tail.clone()], w).unwrap(),
            "Go left now. Wait left now."
        );
        tail.tokens[1].dtw_ms = Some(2800 - w.start as i64 / 16);
        assert!(merge(&p, &[tail], w).is_err());
        native.tokens[1].dtw_ms = None;
        assert!(prefix_dtw(&[native.clone()], range, "Go left now.").is_err());
        native.tokens[1].dtw_ms = Some(100);
        assert_eq!(
            prefix_dtw(&[native], range, "Go left now.").unwrap_err(),
            "InvalidDtwLandmark"
        );
    }
    #[test]
    fn aligned_prefix_reconstructs_full_source_and_keeps_repetition() {
        let range = SampleRange {
            start: 16000,
            end: 80000,
        };
        let native = segment(
            " Go left now. Wait",
            &[
                (" Go", 0, 300),
                (" left", 1800, 2050),
                (" now.", 2050, 2300),
                (" Wait", 2500, 3000),
            ],
        );
        let old = prefix(&[native.clone()], range, "Go left now.").unwrap();
        let w = window(&old, &old, range).unwrap();
        assert!(w.start > range.start);
        let offset = (w.start - range.start) / 16;
        let tail = segment(
            " left now. Wait left now.",
            &[
                (" left", 1800 - offset as i64, 2050 - offset as i64),
                (" now.", 2050 - offset as i64, 2300 - offset as i64),
                (
                    " Wait left now.",
                    2500 - offset as i64,
                    3500 - offset as i64,
                ),
            ],
        );
        assert_eq!(
            merge(&old, &[tail], w).unwrap(),
            "Go left now. Wait left now."
        );
        let bad = segment(
            " right now. Wait",
            &[(" right", 0, 200), (" now.", 200, 400)],
        );
        assert!(merge(&old, &[bad], w).is_err());
    }
    #[test]
    fn invalid_timing_coverage_and_inconsistent_boundaries_reject_cut() {
        let range = SampleRange {
            start: 0,
            end: 64000,
        };
        let native = segment(
            " Go left now. Wait",
            &[
                (" Go", 0, 300),
                (" left", 1800, 2050),
                (" now.", 2050, 2300),
                (" Wait", 2500, 3000),
            ],
        );
        let p = prefix(&[native.clone()], range, "Go left now.").unwrap();
        let mut bad = native.clone();
        bad.tokens[2].end_ms = bad.tokens[2].start_ms;
        assert_eq!(
            prefix(&[bad], range, "Go left now.").unwrap_err(),
            "InvalidOrZeroLengthWordTime"
        );
        let mut bad = native.clone();
        bad.tokens[1].byte_start += 1;
        assert!(prefix(&[bad], range, "Go left now.").is_err());
        let mut drift = prefix(&[native], range, "Go left now.").unwrap();
        drift.end += 2561;
        assert!(window(&p, &drift, range).is_none());
        assert!(window(
            &p,
            &p,
            SampleRange {
                start: 0,
                end: 10000
            }
        )
        .is_none());
    }
}

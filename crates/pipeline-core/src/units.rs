//! Reversible text units. No audio trimming or semantic-finality claim.
use echosub_audio_core::SegmentIdentity;

pub const MAX_UNIT_BYTES: usize = 384;
#[derive(Clone)]
pub(crate) struct Unit {
    pub prefix: String,
    pub source: String,
    pub closed: bool,
}
#[derive(Default)]
pub(crate) struct Units {
    identity: Option<SegmentIdentity>,
    delivered: String,
    last: Option<Unit>,
    pub hold_reason: Option<&'static str>,
}
impl Units {
    pub fn reset(&mut self) {
        *self = Self::default();
    }
    pub fn select(
        &mut self,
        id: SegmentIdentity,
        stable: &str,
        observed: &str,
        language: &str,
    ) -> Option<Unit> {
        if self.identity != Some(id) || !stable.starts_with(&self.delivered) {
            self.reset();
            self.identity = Some(id);
        } else if self
            .last
            .as_ref()
            .is_some_and(|u| !stable.starts_with(&u.prefix))
        {
            // A correction in the mutable tail does not invalidate earlier closed units.
            self.last = None;
        }
        self.hold_reason = None;
        let rest = &stable[self.delivered.len()..];
        let start = stable.len() - rest.trim_start().len();
        let tail = &stable[start..];
        if tail.is_empty() {
            self.hold_reason = Some("EmptyTail");
            return None;
        }
        let limit = tail
            .char_indices()
            .take_while(|(i, c)| i + c.len_utf8() <= MAX_UNIT_BYTES)
            .last()
            .map_or(0, |(i, c)| i + c.len_utf8());
        let boundary = tail[..limit].char_indices().find_map(|(i, c)| {
            let end = i + c.len_utf8();
            let before = &tail[..i];
            let after = tail[end..].chars().next();
            let decimal = c == '.'
                && before.chars().last().is_some_and(|x| x.is_ascii_digit())
                && after.is_some_and(|x| x.is_ascii_digit());
            let word = before
                .split_whitespace()
                .last()
                .unwrap_or("")
                .to_ascii_lowercase();
            let abbreviation = c == '.'
                && (matches!(
                    word.as_str(),
                    "mr" | "mrs" | "ms" | "dr" | "prof" | "vs" | "e.g" | "i.e"
                ) || word.len() == 1 && word.chars().all(|x| x.is_ascii_alphabetic()));
            let sentence = ".!?。！？".contains(c)
                && !decimal
                && !abbreviation
                && (language != "en" || after.is_none_or(|x| x.is_whitespace()));
            // Commas alone are too ambiguous (conditions, lists, Japanese inflection).
            let clause = ";；".contains(c)
                && before.chars().count() >= 12
                && (language != "en" || before.split_whitespace().count() >= 4);
            (sentence || clause).then_some(end)
        });
        let (end, closed) = boundary.map_or_else(
            || {
                let end = if limit < tail.len() && language == "en" {
                    tail[..limit]
                        .char_indices()
                        .filter(|(_, c)| c.is_whitespace())
                        .last()
                        .map_or(0, |(i, _)| i)
                } else {
                    limit
                };
                (end, false)
            },
            |end| (end, true),
        );
        let source = tail[..end].trim_end();
        if source.chars().count() < if closed { 2 } else { 6 }
            || !closed && language == "en" && source.split_whitespace().count() < 3
        {
            self.hold_reason = Some("TooShort");
            return None;
        }
        let unit = Unit {
            prefix: stable[..start + source.len()].into(),
            source: source.into(),
            closed,
        };
        if language == "en" {
            if let Some(reason) = english_fragment(
                &unit.source,
                unit.closed,
                observed.strip_prefix(&unit.prefix).unwrap_or(""),
                source.len() < tail.trim_end().len(),
            ) {
                self.hold_reason = Some(reason);
                return None;
            }
        }
        if self
            .last
            .as_ref()
            .is_some_and(|old| old.prefix == unit.prefix && old.source == unit.source)
        {
            self.hold_reason = Some("AlreadyTranslated");
            return None;
        }
        Some(unit)
    }
    pub fn applied(&mut self, unit: Unit) {
        if unit.closed {
            self.delivered = unit.prefix.clone();
        }
        self.last = Some(unit);
    }
}

// A bounded lexical veto, not a grammar or meaning classifier. Only preview units
// reach this function; final ASR always goes through whole-source translation.
fn english_fragment(
    source: &str,
    closed: bool,
    remainder: &str,
    truncated: bool,
) -> Option<&'static str> {
    let lower = source.to_ascii_lowercase();
    let words: Vec<&str> = lower
        .split_whitespace()
        .map(|word| word.trim_matches(|c: char| c.is_ascii_punctuation()))
        .collect();
    let Some(&last) = words.last() else {
        return None;
    };
    // Even Whisper punctuation must not close "until I" or "unless we can".
    if let Some(condition) = words.iter().rposition(|word| condition_word(word)) {
        let pending = &words[condition + 1..];
        // "before"/"after" can also stand alone as adverbs, so require a tail.
        if pending.is_empty() && !matches!(words[condition], "before" | "after")
            || !pending.is_empty()
                && pending.iter().all(|word| {
                    matches!(
                        *word,
                        "i" | "you"
                            | "he"
                            | "she"
                            | "it"
                            | "we"
                            | "they"
                            | "can"
                            | "could"
                            | "will"
                            | "would"
                            | "shall"
                            | "should"
                            | "may"
                            | "might"
                            | "must"
                            | "do"
                            | "does"
                            | "did"
                            | "am"
                            | "is"
                            | "are"
                            | "was"
                            | "were"
                            | "be"
                            | "have"
                            | "has"
                            | "had"
                            | "not"
                    )
                })
        {
            return Some("IncompleteCondition");
        }
    }
    let numeric = last.parse::<f64>().is_ok_and(f64::is_finite)
        || matches!(
            last,
            "zero"
                | "one"
                | "two"
                | "three"
                | "four"
                | "five"
                | "six"
                | "seven"
                | "eight"
                | "nine"
                | "ten"
                | "eleven"
                | "twelve"
                | "thirteen"
                | "fourteen"
                | "fifteen"
                | "sixteen"
                | "seventeen"
                | "eighteen"
                | "nineteen"
                | "twenty"
                | "thirty"
                | "forty"
                | "fifty"
                | "sixty"
                | "seventy"
                | "eighty"
                | "ninety"
                | "hundred"
                | "thousand"
                | "million"
        );
    // Punctuation alone does not provide the missing object in "There are three.".
    if numeric && words.len() == 3 && words[0] == "there" && matches!(words[1], "is" | "are") {
        return Some("IncompleteNumber");
    }
    if closed || truncated {
        return None;
    }
    // Use an unstable continuation only to defer; never send it as source/context.
    let following = remainder
        .trim_start_matches(|c: char| c.is_whitespace() || c.is_ascii_punctuation())
        .split_whitespace()
        .next()
        .unwrap_or("");
    if condition_word(&following.to_ascii_lowercase()) {
        return Some("ConditionContinuation");
    }
    if numeric {
        return Some("IncompleteNumber");
    }
    if matches!(
        last,
        "a" | "an"
            | "the"
            | "of"
            | "to"
            | "with"
            | "without"
            | "near"
            | "into"
            | "and"
            | "or"
            | "because"
    ) {
        Some("DanglingWord")
    } else {
        None
    }
}

fn condition_word(word: &str) -> bool {
    matches!(
        word,
        "until" | "unless" | "if" | "when" | "before" | "after"
    )
}

//! Reversible text units. No audio trimming or semantic-finality claim.
use echosub_audio_core::SegmentIdentity;

pub const MAX_UNIT_BYTES: usize = 384;
#[derive(Clone)]
pub(crate) struct Unit {
    pub prefix: String,
    pub source: String,
    pub closed: bool,
    pub promoted: bool,
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
        supported: bool,
    ) -> Option<Unit> {
        if self.identity != Some(id) || !stable.starts_with(&self.delivered) {
            self.reset();
            self.identity = Some(id);
        } else if self
            .last
            .as_ref()
            .is_some_and(|u| !(if u.promoted { observed } else { stable }).starts_with(&u.prefix))
        {
            // A correction in the mutable tail does not invalidate earlier closed units.
            self.last = None;
        }
        self.hold_reason = None;
        let extended = if supported && language == "en" {
            supported_tail(stable, observed)
        } else {
            None
        };
        let promoted = extended.is_some();
        let selected = extended.as_deref().unwrap_or(stable);
        let rest = &selected[self.delivered.len()..];
        let start = selected.len() - rest.trim_start().len();
        let tail = &selected[start..];
        if tail.is_empty() {
            self.hold_reason = Some("EmptyTail");
            return None;
        }
        let limit = tail
            .char_indices()
            .take_while(|(i, c)| i + c.len_utf8() <= MAX_UNIT_BYTES)
            .last()
            .map_or(0, |(i, c)| i + c.len_utf8());
        let mut repair_head = false;
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
            // After a delivered clause, a bare "No." is a possible repair marker.
            // Join only the selected (stable) following sentence, never observed text.
            if sentence
                && language == "en"
                && !self.delivered.is_empty()
                && before.trim().eq_ignore_ascii_case("no")
                && c != '?'
            {
                repair_head = true;
                return None;
            }
            // Commas alone are too ambiguous (conditions, lists, Japanese inflection).
            let clause = ";；".contains(c)
                && before.chars().count() >= 12
                && (language != "en" || before.split_whitespace().count() >= 4);
            (sentence || clause).then_some(end)
        });
        if repair_head && boundary.is_none() {
            self.hold_reason = Some("IncompleteRepair");
            return None;
        }
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
        let mut source = tail[..end].trim_end();
        if repair_head {
            // "No. Take ..." can still be read as an abbreviation for "number".
            // Replay the preceding delivered unit as part of the actual source,
            // rather than relying on a model's optional context interpretation.
            let Some(previous) = self.last.as_ref().filter(|unit| unit.closed) else {
                self.hold_reason = Some("IncompleteRepair");
                return None;
            };
            // Unit is internal state, not an imported history record. Still
            // validate offsets before subtraction and UTF-8 slicing.
            let Some(previous_start) = previous.prefix.len().checked_sub(previous.source.len())
            else {
                self.hold_reason = Some("InvalidRepair");
                return None;
            };
            let Some(repair_end) = start.checked_add(source.len()) else {
                self.hold_reason = Some("InvalidRepair");
                return None;
            };
            let Some(repair_len) = repair_end.checked_sub(previous_start) else {
                self.hold_reason = Some("InvalidRepair");
                return None;
            };
            let Some(repaired) = selected.get(previous_start..repair_end) else {
                self.hold_reason = Some("InvalidRepair");
                return None;
            };
            if repair_len > MAX_UNIT_BYTES {
                self.hold_reason = Some("RepairTooLong");
                return None;
            }
            source = repaired;
        }
        if source.chars().count() < if closed { 2 } else { 6 }
            || !closed && language == "en" && source.split_whitespace().count() < 3
        {
            self.hold_reason = Some("TooShort");
            return None;
        }
        let unit = Unit {
            prefix: selected[..start + tail[..end].trim_end().len()].into(),
            source: source.into(),
            closed: closed && !promoted,
            promoted,
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
            // Once the previously promoted sentence is genuinely stable, commit
            // its boundary without translating it twice, then inspect the tail.
            if !promoted && unit.closed && self.last.as_ref().is_some_and(|u| u.promoted) {
                self.delivered = unit.prefix.clone();
                self.last = Some(unit);
                return self.select(id, stable, observed, language, supported);
            }
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
    // A trailing comma-delimited "no" signals a correction whose action is absent.
    // Preserve standalone answers such as "No." and "The answer is no.".
    let comma_repair = lower.split(',').skip(1).any(|part| {
        part.trim()
            .trim_matches(|c: char| c.is_ascii_punctuation())
            .eq("no")
    });
    if comma_repair && (last == "no" || !closed) {
        return Some("IncompleteRepair");
    }
    // A punctuation mark cannot supply a missing action or condition predicate.
    if words.len() >= 3 && matches!(last, "not" | "never") {
        return Some("DanglingWord");
    }
    // "only." may be a premature Whisper boundary before "only if ...".
    // An observed qualifier can veto a preview, but never supplies translated text.
    if last == "only" {
        return Some("IncompleteCondition");
    }
    if words.iter().any(|w| condition_word(w))
        && matches!(
            last,
            "am" | "is" | "are" | "was" | "were" | "be" | "been" | "being" | "has" | "have" | "had"
        )
    {
        return Some("IncompleteCondition");
    }
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
        .unwrap_or("")
        .trim_matches(|c: char| c.is_ascii_punctuation());
    if source.trim_end().ends_with(',') && following.eq_ignore_ascii_case("no") {
        return Some("IncompleteRepair");
    }
    if condition_word(&following.to_ascii_lowercase()) || following.eq_ignore_ascii_case("only") {
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

// One observation may support only a reversible, sentence-ended lexical tail.
// Never change Agreement's stable prefix or commit this unit as delivered.
fn supported_tail(stable: &str, observed: &str) -> Option<String> {
    let stable = stable.trim_end();
    if stable.split_whitespace().count() < 3
        || stable.chars().any(|c| ".!?;".contains(c))
        || stable
            .split_whitespace()
            .any(|w| condition_word(&w.to_ascii_lowercase()))
        || !matches!(
            english_fragment(stable, false, "", false),
            None | Some("DanglingWord")
        )
    {
        return None;
    }
    let suffix = observed.strip_prefix(stable)?;
    if !suffix.starts_with(char::is_whitespace) || suffix.len() > 80 {
        return None;
    }
    let suffix = suffix.trim();
    if !suffix.ends_with(['.', '!', '?']) {
        return None;
    }
    let words: Vec<&str> = suffix[..suffix.len() - 1].split_whitespace().collect();
    if !(1..=2).contains(&words.len())
        || words.iter().any(|w| {
            !w.chars().all(|c| c.is_ascii_alphabetic())
                || english_fragment(&format!("There are {w}"), false, "", false)
                    == Some("IncompleteNumber")
                || w.len() < 2
                || matches!(
                    w.to_ascii_lowercase().as_str(),
                    "not"
                        | "no"
                        | "never"
                        | "cannot"
                        | "can"
                        | "could"
                        | "will"
                        | "would"
                        | "should"
                        | "must"
                        | "may"
                        | "might"
                        | "mr"
                        | "mrs"
                        | "ms"
                        | "dr"
                        | "prof"
                        | "vs"
                )
                || condition_word(&w.to_ascii_lowercase())
        })
    {
        return None;
    }
    let result = observed.trim_end();
    if result.len() > MAX_UNIT_BYTES || english_fragment(result, false, "", false).is_some() {
        return None;
    }
    Some(result.to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use echosub_audio_core::AudioIdentity;

    fn id(epoch: u64) -> SegmentIdentity {
        SegmentIdentity {
            audio: AudioIdentity {
                session_id: 1,
                epoch,
            },
            segment_id: 1,
        }
    }

    #[test]
    fn complete_repairs_enforce_combined_byte_cap_at_utf8_boundary() {
        for multibyte in [false, true] {
            for bytes in [MAX_UNIT_BYTES - 1, MAX_UNIT_BYTES, MAX_UNIT_BYTES + 1] {
                let mut units = Units::default();
                let first = "Take the left path.";
                let unit = units.select(id(1), first, first, "en", false).unwrap();
                units.applied(unit);
                let head = format!("{first} No. Take the right path past ");
                let marker = if multibyte { "다리" } else { "bridge" };
                let padding = bytes - head.len() - marker.len() - 1;
                let complete = format!("{head}{}{marker}.", "a".repeat(padding));
                assert_eq!(complete.len(), bytes);
                let selected = units.select(id(1), &complete, &complete, "en", false);
                if bytes > MAX_UNIT_BYTES {
                    assert!(selected.is_none());
                    assert_eq!(units.hold_reason, Some("RepairTooLong"));
                } else {
                    assert_eq!(selected.unwrap().source, complete);
                    assert_eq!(units.hold_reason, None);
                }
            }
        }
    }

    #[test]
    fn repair_units_preserve_boundaries_cap_and_identity_reset() {
        let mut units = Units::default();
        let first = "Take the left path.";
        let unit = units.select(id(1), first, first, "en", false).unwrap();
        units.applied(unit);
        let stable = format!("{first} No.");
        let observed = format!("{stable} Take the right path.");
        assert!(units
            .select(id(1), &stable, &observed, "en", false)
            .is_none());
        assert_eq!(units.hold_reason, Some("IncompleteRepair"));
        let long = format!("{stable} {}", "keep moving toward the bridge ".repeat(30));
        assert!(units.select(id(1), &long, &long, "en", false).is_none());
        assert_eq!(units.hold_reason, Some("IncompleteRepair"));
        let complete = format!("{stable} Take the right path. Keep going.");
        let unit = units
            .select(id(1), &complete, &complete, "en", false)
            .unwrap();
        assert_eq!(unit.source, "Take the left path. No. Take the right path.");
        assert!(unit.source.len() <= MAX_UNIT_BYTES);
        units.applied(unit);
        assert_eq!(
            units
                .select(id(1), &complete, &complete, "en", false)
                .unwrap()
                .source,
            "Keep going."
        );
        // A new epoch cannot inherit the prior delivered sentence or repair state.
        assert_eq!(
            units
                .select(id(2), "No.", "No.", "en", false)
                .unwrap()
                .source,
            "No."
        );
    }

    #[test]
    fn repair_units_do_not_change_other_languages_or_normal_answers() {
        let mut units = Units::default();
        for answer in ["No.", "No!", "No?", "No, thank you.", "The answer is no."] {
            units.reset();
            assert_eq!(
                units
                    .select(id(1), answer, answer, "en", false)
                    .unwrap()
                    .source,
                answer
            );
        }
        assert_eq!(
            english_fragment("Take the left path, no.", true, "", false),
            Some("IncompleteRepair")
        );
        assert_eq!(
            english_fragment("Take the left path, no, take the", false, "", false),
            Some("IncompleteRepair")
        );
        let first = units
            .select(id(2), "進んで。", "進んで。", "ja", false)
            .unwrap();
        units.applied(first);
        assert_eq!(
            units
                .select(
                    id(2),
                    "進んで。No. 次へ。",
                    "進んで。No. 次へ。",
                    "ja",
                    false
                )
                .unwrap()
                .source,
            "No."
        );
    }
}

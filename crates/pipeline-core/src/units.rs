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
}
impl Units {
    pub fn reset(&mut self) {
        *self = Self::default();
    }
    pub fn select(&mut self, id: SegmentIdentity, stable: &str, language: &str) -> Option<Unit> {
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
        let rest = &stable[self.delivered.len()..];
        let start = stable.len() - rest.trim_start().len();
        let tail = &stable[start..];
        if tail.is_empty() {
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
            return None;
        }
        let unit = Unit {
            prefix: stable[..start + source.len()].into(),
            source: source.into(),
            closed,
        };
        if self
            .last
            .as_ref()
            .is_some_and(|old| old.prefix == unit.prefix && old.source == unit.source)
        {
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

//! Conservative text LocalAgreement-2 candidate, not a confidence estimate.
use echosub_audio_core::SegmentIdentity;

#[derive(Default)]
pub(crate) struct Agreement {
    previous: Option<(SegmentIdentity, String)>,
}
impl Agreement {
    pub fn reset(&mut self) {
        self.previous = None;
    }
    pub fn observe(&mut self, id: SegmentIdentity, text: &str, language: &str) -> String {
        let candidate = self
            .previous
            .as_ref()
            .filter(|(old, _)| *old == id)
            .map(|(_, old)| {
                let bytes: usize = old
                    .chars()
                    .zip(text.chars())
                    .take_while(|(a, b)| a == b)
                    .map(|(c, _)| c.len_utf8())
                    .sum();
                let common = &text[..bytes];
                let capped = common
                    .char_indices()
                    .take_while(|(i, c)| i + c.len_utf8() <= crate::MAX_STABLE_BYTES)
                    .last()
                    .map_or(0, |(i, c)| i + c.len_utf8());
                let common = &common[..capped];
                let boundary = |c: char| c.is_whitespace() || ".!?;:,。！？、".contains(c);
                let end = if language == "en"
                    && text[common.len()..].chars().next().is_some_and(boundary)
                {
                    // Newly observed boundary confirms the last shared word is complete.
                    common.len()
                } else if language == "en" {
                    common
                        .char_indices()
                        .filter(|(_, c)| boundary(*c))
                        .last()
                        .map_or(0, |(i, c)| i + c.len_utf8())
                } else if common.chars().last().is_some_and(boundary) {
                    common.len()
                } else {
                    // CJK has no space-delimited word boundary. Withhold the last character.
                    common.char_indices().last().map_or(0, |(i, _)| i)
                };
                let prefix = common[..end].trim_end();
                if prefix.chars().count() < 6
                    || (language == "en" && prefix.split_whitespace().count() < 3)
                {
                    String::new()
                } else {
                    prefix.to_owned()
                }
            })
            .unwrap_or_default();
        self.previous = Some((id, text.to_owned()));
        candidate
    }
}

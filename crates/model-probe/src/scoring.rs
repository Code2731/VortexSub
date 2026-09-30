#![cfg_attr(not(feature = "native"), allow(dead_code))]
use serde::Serialize;

#[derive(Serialize)]
pub struct Score {
    pub metric: &'static str,
    pub edits: usize,
    pub reference_units: usize,
    pub error_rate: Option<f64>,
    pub unexpected_text: bool,
}

fn distance<T: Eq>(reference: &[T], hypothesis: &[T]) -> usize {
    let mut previous: Vec<usize> = (0..=hypothesis.len()).collect();
    let mut current = vec![0; hypothesis.len() + 1];
    for (i, expected) in reference.iter().enumerate() {
        current[0] = i + 1;
        for (j, actual) in hypothesis.iter().enumerate() {
            current[j + 1] = (previous[j + 1] + 1)
                .min(current[j] + 1)
                .min(previous[j] + usize::from(expected != actual));
        }
        std::mem::swap(&mut previous, &mut current);
    }
    previous[hypothesis.len()]
}

pub fn score(language: &str, reference: &str, hypothesis: &str) -> Score {
    let normalized = |text: &str| {
        text.to_lowercase()
            .chars()
            .filter(|c| c.is_alphanumeric() || c.is_whitespace())
            .collect::<String>()
    };
    let reference = normalized(reference);
    let hypothesis = normalized(hypothesis);
    let (metric, edits, units) = if language == "en" {
        let expected: Vec<&str> = reference.split_whitespace().collect();
        let actual: Vec<&str> = hypothesis.split_whitespace().collect();
        ("WER", distance(&expected, &actual), expected.len())
    } else {
        let expected: Vec<char> = reference.chars().filter(|c| !c.is_whitespace()).collect();
        let actual: Vec<char> = hypothesis.chars().filter(|c| !c.is_whitespace()).collect();
        ("CER", distance(&expected, &actual), expected.len())
    };
    Score {
        metric,
        edits,
        reference_units: units,
        error_rate: (units != 0).then(|| edits as f64 / units as f64),
        unexpected_text: units == 0 && !hypothesis.trim().is_empty(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn deletion_insertion_and_substitution_count() {
        assert_eq!(score("en", "one two three", "one four").edits, 2);
        assert_eq!(score("ja", "左へ進もう", "右へ進もう").edits, 1);
        assert_eq!(score("ko", "왼쪽 길", "왼쪽길").edits, 0);
    }
    #[test]
    fn silence_never_produces_a_zero_error_rate() {
        let result = score("en", "", "Thank you");
        assert_eq!(result.error_rate, None);
        assert!(result.unexpected_text);
    }
}

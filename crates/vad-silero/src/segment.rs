use crate::{Backend, Detector, Error, FRAME};
use echosub_audio_core::{
    AudioFrame, AudioIdentity, AudioTail, SampleRange, SpeechEvent, VadSegmenter, VadSettings,
};
#[cfg(test)]
mod tests {
    use super::*;
    struct Probability(f32);
    impl Backend for Probability {
        fn infer(&mut self, _: &[f32; 576], _: &[f32; 256]) -> Result<(f32, [f32; 256]), Error> {
            Ok((self.0, [0.; 256]))
        }
    }
    const ID: AudioIdentity = AudioIdentity {
        session_id: 1,
        epoch: 1,
    };
    #[test]
    fn true_tail_is_never_extended_by_model_padding() {
        let pcm = vec![0.2; 512 * 6 + 17];
        let mut d = Detector::new(Probability(0.9), ID, 0);
        let result = segment(&mut d, ID, &pcm).unwrap();
        assert_eq!(result.ranges.len(), 1);
        assert_eq!(result.ranges[0].end, pcm.len() as u64);
        assert_eq!(result.model_calls, 7);
    }
    #[test]
    fn nonzero_negative_probability_never_creates_asr_range() {
        let mut d = Detector::new(Probability(0.01), ID, 0);
        let result = segment(&mut d, ID, &vec![0.2; 16000]).unwrap();
        assert!(result.ranges.is_empty());
        assert_eq!(result.model_calls, 32);
    }
}
pub struct Segmented {
    pub ranges: Vec<SampleRange>,
    pub model_calls: u64,
}
/// File fixtures are independent streams. Caller resets recurrence before entry.
/// Only final ranges are returned; partial inference remains disabled.
pub fn segment<B: Backend>(
    detector: &mut Detector<B>,
    identity: AudioIdentity,
    pcm: &[f32],
) -> Result<Segmented, Error> {
    if pcm.is_empty() || pcm.len() > 128000 {
        return Err(Error::InvalidFrame);
    }
    let mut core =
        VadSegmenter::new(identity, 0, VadSettings::default()).map_err(|_| Error::InvalidFrame)?;
    let before = detector.calls();
    let mut ranges = Vec::new();
    for (i, chunk) in pcm.chunks(FRAME).enumerate() {
        let range = SampleRange {
            start: (i * FRAME) as u64,
            end: (i * FRAME + chunk.len()) as u64,
        };
        let probability = detector.probability(identity, range, chunk)?;
        let p = if chunk.iter().any(|x| *x != 0.) {
            Some(probability)
        } else {
            None
        };
        let now = range.end * 1_000_000_000 / 16000;
        let events = if chunk.len() == FRAME {
            let mut samples = [0.; FRAME];
            samples.copy_from_slice(chunk);
            core.push(
                &AudioFrame {
                    identity,
                    range,
                    samples,
                },
                p,
                now,
            )
        } else {
            core.close_with_tail(
                &AudioTail {
                    identity,
                    range,
                    samples: chunk.to_vec(),
                },
                p,
                now,
            )
        }
        .map_err(|_| Error::InvalidFrame)?;
        collect(events, detector, &mut ranges);
    }
    if pcm.len() % FRAME == 0 {
        collect(
            core.close(pcm.len() as u64 * 1_000_000_000 / 16000)
                .map_err(|_| Error::InvalidFrame)?,
            detector,
            &mut ranges,
        );
    }
    Ok(Segmented {
        ranges,
        model_calls: detector.calls() - before,
    })
}
fn collect<B: Backend>(
    events: Vec<SpeechEvent>,
    d: &mut Detector<B>,
    ranges: &mut Vec<SampleRange>,
) {
    for event in events {
        match event {
            SpeechEvent::Final { segment, .. } => ranges.push(segment.pcm_range),
            SpeechEvent::ModelReset(_) => d.reset_recurrent(),
            _ => {}
        }
    }
}

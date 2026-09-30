//! Silero v6 recurrent wire state. Model work belongs to the processing owner.
use echosub_audio_core::{AudioIdentity, SampleRange};

pub const FRAME: usize = 512;
pub const CONTEXT: usize = 64;
pub const STATE: usize = 256;
mod segment;
pub use segment::{segment, Segmented};
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Error {
    StaleIdentity,
    InvalidFrame,
    InvalidOutput,
    Inference,
    Model,
    Runtime,
}

/// Backend receives [1,576] audio, [2,1,128] state and scalar 16000 i64.
/// This trait also permits deterministic wire-contract fixtures without a model.
pub trait Backend {
    fn infer(
        &mut self,
        input: &[f32; FRAME + CONTEXT],
        state: &[f32; STATE],
    ) -> Result<(f32, [f32; STATE]), Error>;
}

pub struct Detector<B> {
    backend: B,
    identity: AudioIdentity,
    cursor: u64,
    state: [f32; STATE],
    context: [f32; CONTEXT],
    calls: u64,
}
impl<B: Backend> Detector<B> {
    pub fn new(backend: B, identity: AudioIdentity, start: u64) -> Self {
        Self {
            backend,
            identity,
            cursor: start,
            state: [0.; STATE],
            context: [0.; CONTEXT],
            calls: 0,
        }
    }
    /// Gap/device/Pause recovery must advance epoch before the next frame.
    pub fn reset(&mut self, identity: AudioIdentity, start: u64) -> Result<(), Error> {
        if identity.session_id < self.identity.session_id
            || (identity.session_id == self.identity.session_id
                && identity.epoch <= self.identity.epoch)
        {
            return Err(Error::StaleIdentity);
        }
        self.identity = identity;
        self.cursor = start;
        self.clear();
        Ok(())
    }
    fn clear(&mut self) {
        self.state.fill(0.);
        self.context.fill(0.);
    }
    /// Segmenter ModelReset events reset recurrence without changing PCM identity.
    pub fn reset_recurrent(&mut self) {
        self.clear();
    }
    pub fn calls(&self) -> u64 {
        self.calls
    }
    /// A short final frame is padded only for inference; range retains real PCM.
    pub fn probability(
        &mut self,
        identity: AudioIdentity,
        range: SampleRange,
        pcm: &[f32],
    ) -> Result<f32, Error> {
        if identity != self.identity {
            return Err(Error::StaleIdentity);
        }
        if pcm.is_empty()
            || pcm.len() > FRAME
            || range.start != self.cursor
            || range.end.checked_sub(range.start) != Some(pcm.len() as u64)
            || pcm.iter().any(|x| !x.is_finite() || x.abs() > 1.)
        {
            return Err(Error::InvalidFrame);
        }
        if pcm.iter().all(|x| *x == 0.) {
            self.clear();
            self.cursor = range.end;
            return Ok(0.);
        }
        let mut input = [0.; FRAME + CONTEXT];
        input[..CONTEXT].copy_from_slice(&self.context);
        input[CONTEXT..CONTEXT + pcm.len()].copy_from_slice(pcm);
        self.calls += 1;
        let result = self.backend.infer(&input, &self.state);
        let (probability, state) = match result {
            Ok((p, s))
                if p.is_finite() && (0.0..=1.0).contains(&p) && s.iter().all(|x| x.is_finite()) =>
            {
                (p, s)
            }
            Ok(_) => {
                self.clear();
                return Err(Error::InvalidOutput);
            }
            Err(e) => {
                self.clear();
                return Err(e);
            }
        };
        self.state = state;
        self.context.copy_from_slice(&input[FRAME..]);
        self.cursor = range.end;
        Ok(probability)
    }
}

#[cfg(feature = "onnx")]
pub mod native;

#[cfg(test)]
mod tests {
    use super::*;
    const ID: AudioIdentity = AudioIdentity {
        session_id: 1,
        epoch: 1,
    };
    struct Mock {
        calls: usize,
        bad: bool,
    }
    impl Backend for Mock {
        fn infer(
            &mut self,
            input: &[f32; 576],
            state: &[f32; 256],
        ) -> Result<(f32, [f32; 256]), Error> {
            if self.calls == 0 {
                assert!(input[..64].iter().all(|x| *x == 0.));
                assert_eq!(*state, [0.; 256]);
            }
            self.calls += 1;
            Ok((
                if self.bad { f32::NAN } else { 0.8 },
                [self.calls as f32; 256],
            ))
        }
    }
    #[test]
    fn silence_skips_native_and_real_tail_cursor_is_preserved() {
        let mut d = Detector::new(
            Mock {
                calls: 0,
                bad: false,
            },
            ID,
            0,
        );
        for start in (0..16000 * 600).step_by(512) {
            d.probability(
                ID,
                SampleRange {
                    start,
                    end: start + 512,
                },
                &[0.; 512],
            )
            .unwrap();
        }
        assert_eq!(d.calls(), 0);
        let start = d.cursor;
        assert_eq!(
            d.probability(
                ID,
                SampleRange {
                    start,
                    end: start + 17
                },
                &[0.1; 17]
            ),
            Ok(0.8)
        );
        assert_eq!(d.cursor, start + 17);
    }
    #[test]
    fn recurrence_and_epoch_reset_clear_context_and_reject_old_frames() {
        struct Recurrence;
        impl Backend for Recurrence {
            fn infer(
                &mut self,
                x: &[f32; 576],
                s: &[f32; 256],
            ) -> Result<(f32, [f32; 256]), Error> {
                if s[0] == 0. {
                    assert_eq!(&x[..64], &[0.; 64]);
                } else {
                    assert_eq!(&x[..64], &[0.25; 64]);
                }
                Ok((0.5, [1.; 256]))
            }
        }
        let mut d = Detector::new(Recurrence, ID, 0);
        for start in [0, 512] {
            d.probability(
                ID,
                SampleRange {
                    start,
                    end: start + 512,
                },
                &[0.25; 512],
            )
            .unwrap();
        }
        let next = AudioIdentity { epoch: 2, ..ID };
        d.reset(next, 1024).unwrap();
        assert_eq!(
            d.probability(
                ID,
                SampleRange {
                    start: 1024,
                    end: 1536
                },
                &[0.25; 512]
            ),
            Err(Error::StaleIdentity)
        );
        d.probability(
            next,
            SampleRange {
                start: 1024,
                end: 1536,
            },
            &[0.25; 512],
        )
        .unwrap();
        assert_eq!(d.reset(next, 1536), Err(Error::StaleIdentity));
    }
    #[test]
    fn invalid_input_does_not_call_model_and_bad_output_does_not_advance_cursor() {
        let mut d = Detector::new(
            Mock {
                calls: 0,
                bad: true,
            },
            ID,
            0,
        );
        assert_eq!(
            d.probability(ID, SampleRange { start: 0, end: 1 }, &[f32::NAN]),
            Err(Error::InvalidFrame)
        );
        assert_eq!(d.calls(), 0);
        assert_eq!(
            d.probability(ID, SampleRange { start: 0, end: 512 }, &[0.2; 512]),
            Err(Error::InvalidOutput)
        );
        assert_eq!(d.cursor, 0);
        assert_eq!(d.state, [0.; 256]);
    }
}

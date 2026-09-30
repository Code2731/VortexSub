use crate::{
    AudioError, AudioGap, AudioIdentity, GapReason, SampleRange, FRAME_SAMPLES, SAMPLE_RATE,
};
use std::collections::VecDeque;
const RADIUS: usize = 128;
const TAPS: usize = RADIUS * 2 + 1;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AudioFormat {
    rate: u32,
    channels: usize,
}
impl AudioFormat {
    /// Only known mono and FL/FR stereo layouts are supported in this first core unit.
    pub fn new(rate: u32, channels: usize, mask: Option<u32>) -> Result<Self, AudioError> {
        let layout_ok = match channels {
            1 => matches!(mask, None | Some(0x1) | Some(0x4)),
            2 => matches!(mask, None | Some(0x3)),
            _ => false,
        };
        if !matches!(rate, 16_000 | 44_100 | 48_000) || !layout_ok {
            return Err(AudioError::UnsupportedFormat);
        }
        Ok(Self { rate, channels })
    }
    pub fn sample_rate(self) -> u32 {
        self.rate
    }
    pub fn channels(self) -> usize {
        self.channels
    }
}
#[derive(Debug)]
pub struct AudioFrame {
    pub identity: AudioIdentity,
    pub range: SampleRange,
    pub samples: [f32; FRAME_SAMPLES],
}
#[derive(Debug)]
pub struct AudioTail {
    pub identity: AudioIdentity,
    pub range: SampleRange,
    pub samples: Vec<f32>,
}
#[derive(Debug, Default)]
pub struct AudioBatch {
    pub frames: Vec<AudioFrame>,
    pub tail: Option<AudioTail>,
}

/// Stateful, centered 257-tap Blackman-windowed sinc resampling, on the processing thread.
/// Fractional phases are precomputed. The adapter supplies the monotonic origin;
/// output ranges refer to filter centers, not delivery time (look-ahead is not added).
pub struct StreamNormalizer {
    format: AudioFormat,
    identity: AudioIdentity,
    origin: u64,
    received: u64,
    output_count: u64,
    published_end: u64,
    history: VecDeque<f32>,
    history_start: u64,
    kernels: Vec<[f64; TAPS]>,
    phase_step: u64,
    frame: [f32; FRAME_SAMPLES],
    filled: usize,
    closed: bool,
    clipped_values: u64,
}
impl StreamNormalizer {
    pub fn new(format: AudioFormat, identity: AudioIdentity, origin: u64) -> Self {
        let divisor = gcd(format.rate as u64, SAMPLE_RATE as u64);
        let phases = SAMPLE_RATE as usize / divisor as usize;
        let mut kernels = Vec::with_capacity(if format.rate == SAMPLE_RATE {
            0
        } else {
            phases
        });
        if format.rate != SAMPLE_RATE {
            let cutoff = 0.45 * SAMPLE_RATE as f64 / format.rate as f64;
            for phase in 0..phases {
                let fraction = phase as f64 / phases as f64;
                let mut kernel = [0.0; TAPS];
                for (i, value) in kernel.iter_mut().enumerate() {
                    let distance = i as f64 - RADIUS as f64 - fraction;
                    let x = 2.0 * cutoff * distance;
                    let sinc = if x.abs() < 1e-12 {
                        1.0
                    } else {
                        (std::f64::consts::PI * x).sin() / (std::f64::consts::PI * x)
                    };
                    let angle = 2.0 * std::f64::consts::PI * i as f64 / (TAPS - 1) as f64;
                    let window = 0.42 - 0.5 * angle.cos() + 0.08 * (2.0 * angle).cos();
                    *value = 2.0 * cutoff * sinc * window;
                }
                let sum: f64 = kernel.iter().sum();
                for value in &mut kernel {
                    *value /= sum;
                }
                kernels.push(kernel);
            }
        }
        Self {
            format,
            identity,
            origin,
            received: 0,
            output_count: 0,
            published_end: origin,
            history: VecDeque::with_capacity(TAPS + 8),
            history_start: 0,
            kernels,
            phase_step: format.rate as u64 / divisor,
            frame: [0.0; FRAME_SAMPLES],
            filled: 0,
            closed: false,
            clipped_values: 0,
        }
    }
    pub fn look_ahead_s(&self) -> f64 {
        if self.format.rate == SAMPLE_RATE {
            0.0
        } else {
            RADIUS as f64 / self.format.rate as f64
        }
    }
    /// Counts clipped mixed-input values and FIR-output values separately from time.
    pub fn clipped_values(&self) -> u64 {
        self.clipped_values
    }
    pub fn input_end_sample(&self) -> Result<u64, AudioError> {
        self.end_for(self.received)
    }
    fn end_for(&self, received: u64) -> Result<u64, AudioError> {
        let count = received as u128 * SAMPLE_RATE as u128 / self.format.rate as u128;
        let count = u64::try_from(count).map_err(|_| AudioError::TimestampOverflow)?;
        self.origin
            .checked_add(count)
            .ok_or(AudioError::TimestampOverflow)
    }
    /// Borrowed float32 interleaved PCM; a packet is limited to two source seconds.
    /// Validate the entire packet before mutating state. Finite amplitudes are clamped.
    pub fn push(&mut self, pcm: &[f32]) -> Result<AudioBatch, AudioError> {
        if self.closed {
            return Err(AudioError::ClosedStream);
        }
        if pcm.is_empty()
            || pcm.len() % self.format.channels != 0
            || pcm.len() / self.format.channels > 2 * self.format.rate as usize
        {
            return Err(AudioError::InvalidPacket);
        }
        if pcm.iter().any(|x| !x.is_finite()) {
            return Err(AudioError::NonFiniteSample);
        }
        let received = self
            .received
            .checked_add((pcm.len() / self.format.channels) as u64)
            .ok_or(AudioError::TimestampOverflow)?;
        self.end_for(received)?;
        let mut batch = AudioBatch::default();
        for channels in pcm.chunks_exact(self.format.channels) {
            let mixed =
                channels.iter().map(|x| *x as f64).sum::<f64>() / self.format.channels as f64;
            if !(-1.0..=1.0).contains(&mixed) {
                self.clipped_values = self.clipped_values.saturating_add(1);
            }
            let mono = mixed.clamp(-1.0, 1.0) as f32;
            self.received += 1;
            if self.format.rate == SAMPLE_RATE {
                self.accept(mono, &mut batch);
            } else {
                self.history.push_back(mono);
                self.emit(false, &mut batch);
            }
        }
        Ok(batch)
    }
    fn accept(&mut self, sample: f32, batch: &mut AudioBatch) {
        self.frame[self.filled] = sample;
        self.filled += 1;
        self.output_count += 1;
        if self.filled == FRAME_SAMPLES {
            let end = self.origin + self.output_count;
            batch.frames.push(AudioFrame {
                identity: self.identity,
                range: SampleRange {
                    start: end - FRAME_SAMPLES as u64,
                    end,
                },
                samples: self.frame,
            });
            self.published_end = end;
            self.filled = 0;
        }
    }
    fn emit(&mut self, finishing: bool, batch: &mut AudioBatch) {
        let desired =
            (self.received as u128 * SAMPLE_RATE as u128 / self.format.rate as u128) as u64;
        while self.output_count < desired {
            let center =
                (self.output_count as u128 * self.format.rate as u128 / SAMPLE_RATE as u128) as u64;
            if !finishing && center.saturating_add(RADIUS as u64) >= self.received {
                break;
            }
            let phase = ((self.output_count as u128 * self.phase_step as u128)
                % self.kernels.len() as u128) as usize;
            let mut sum = 0.0;
            for (i, weight) in self.kernels[phase].iter().enumerate() {
                let position = center as i128 + i as i128 - RADIUS as i128;
                if position >= 0 && position < self.received as i128 {
                    let index = (position as u64 - self.history_start) as usize;
                    sum += *weight * self.history[index] as f64;
                }
            }
            if !(-1.0..=1.0).contains(&sum) {
                self.clipped_values = self.clipped_values.saturating_add(1);
            }
            self.accept(sum.clamp(-1.0, 1.0) as f32, batch);
            let next_center =
                (self.output_count as u128 * self.format.rate as u128 / SAMPLE_RATE as u128) as u64;
            let retain_from = next_center.saturating_sub(RADIUS as u64);
            while self.history_start < retain_from && !self.history.is_empty() {
                self.history.pop_front();
                self.history_start += 1;
            }
        }
    }
    /// Close a real stream: zero-extend FIR edges, but never extend its duration.
    /// The final short tail remains separate; no padded 512-frame is claimed as audio.
    pub fn finish(&mut self) -> Result<AudioBatch, AudioError> {
        if self.closed {
            return Err(AudioError::ClosedStream);
        }
        let mut batch = AudioBatch::default();
        if self.format.rate != SAMPLE_RATE {
            self.emit(true, &mut batch);
        }
        if self.filled > 0 {
            let end = self.origin + self.output_count;
            batch.tail = Some(AudioTail {
                identity: self.identity,
                range: SampleRange {
                    start: end - self.filled as u64,
                    end,
                },
                samples: self.frame[..self.filled].to_vec(),
            });
            self.published_end = end;
            self.filled = 0;
        }
        self.closed = true;
        Ok(batch)
    }
    /// Attach a new device/epoch while preserving session time. Buffered old audio
    /// is discarded explicitly as a gap; old batches still carry the old identity.
    pub fn reanchor(
        &mut self,
        format: AudioFormat,
        identity: AudioIdentity,
        origin: u64,
        reason: GapReason,
    ) -> Result<AudioGap, AudioError> {
        if identity.session_id != self.identity.session_id || identity.epoch <= self.identity.epoch
        {
            return Err(AudioError::StaleIdentity);
        }
        if origin < self.input_end_sample()? {
            return Err(AudioError::NonContiguous);
        }
        let gap = AudioGap {
            old_identity: self.identity,
            new_identity: identity,
            range: SampleRange {
                start: self.published_end,
                end: origin,
            },
            reason,
        };
        *self = Self::new(format, identity, origin);
        Ok(gap)
    }
}
fn gcd(mut a: u64, mut b: u64) -> u64 {
    while b != 0 {
        (a, b) = (b, a % b);
    }
    a
}

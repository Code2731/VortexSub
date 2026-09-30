use crate::{
    AudioError, AudioIdentity, JobIdentity, SampleRange, MAX_ROLLING_SAMPLES, MAX_SNAPSHOT_SAMPLES,
    MAX_SNAPSHOT_SLOTS,
};
use std::sync::Arc;

/// Single processing-thread owner; fixed storage, addressed by absolute session samples.
pub struct RollingAudio {
    identity: AudioIdentity,
    samples: Box<[f32]>,
    write: usize,
    len: usize,
    end: u64,
}
impl RollingAudio {
    pub fn new(capacity: usize, identity: AudioIdentity, start: u64) -> Result<Self, AudioError> {
        if capacity == 0 || capacity > MAX_ROLLING_SAMPLES {
            return Err(AudioError::InvalidCapacity);
        }
        Ok(Self {
            identity,
            samples: vec![0.0; capacity].into_boxed_slice(),
            write: 0,
            len: 0,
            end: start,
        })
    }
    pub fn retained_range(&self) -> SampleRange {
        SampleRange {
            start: self.end - self.len as u64,
            end: self.end,
        }
    }
    pub fn capacity(&self) -> usize {
        self.samples.len()
    }
    /// Pin an empty epoch to its first native timestamp without inventing gap PCM.
    pub fn anchor_empty(&mut self, identity: AudioIdentity, start: u64) -> Result<(), AudioError> {
        if identity != self.identity {
            return Err(AudioError::StaleIdentity);
        }
        if self.len != 0 || start < self.end {
            return Err(AudioError::NonContiguous);
        }
        self.end = start;
        Ok(())
    }
    pub fn append(
        &mut self,
        identity: AudioIdentity,
        start: u64,
        pcm: &[f32],
    ) -> Result<(), AudioError> {
        if identity != self.identity {
            return Err(AudioError::StaleIdentity);
        }
        if start != self.end {
            return Err(AudioError::NonContiguous);
        }
        if pcm.is_empty() {
            return Err(AudioError::InvalidPacket);
        }
        if pcm.iter().any(|x| !x.is_finite()) {
            return Err(AudioError::NonFiniteSample);
        }
        if pcm.iter().any(|x| !(-1.0..=1.0).contains(x)) {
            return Err(AudioError::InvalidAmplitude);
        }
        let end = self
            .end
            .checked_add(pcm.len() as u64)
            .ok_or(AudioError::TimestampOverflow)?;
        for &sample in pcm {
            self.samples[self.write] = sample;
            self.write = (self.write + 1) % self.samples.len();
            self.len = (self.len + 1).min(self.samples.len());
        }
        self.end = end;
        Ok(())
    }
    pub fn reset(&mut self, identity: AudioIdentity, start: u64) -> Result<(), AudioError> {
        if identity.session_id != self.identity.session_id || identity.epoch <= self.identity.epoch
        {
            return Err(AudioError::StaleIdentity);
        }
        if start < self.end {
            return Err(AudioError::NonContiguous);
        }
        self.identity = identity;
        self.write = 0;
        self.len = 0;
        self.end = start;
        Ok(())
    }
    fn copy(&self, range: SampleRange, output: &mut [f32]) -> Result<(), AudioError> {
        let retained = self.retained_range();
        if range.start >= range.end {
            return Err(AudioError::InvalidRange);
        }
        if range.start < retained.start || range.end > retained.end {
            return Err(AudioError::RangeUnavailable);
        }
        let first = (self.write + self.samples.len() - self.len) % self.samples.len();
        let offset = (range.start - retained.start) as usize;
        for (i, value) in output.iter_mut().enumerate() {
            *value = self.samples[(first + offset + i) % self.samples.len()];
        }
        Ok(())
    }
}

struct Storage {
    samples: Box<[f32]>,
    len: usize,
    key: JobIdentity,
    range: SampleRange,
}
#[derive(Clone)]
pub struct PcmSnapshot {
    storage: Arc<Storage>,
}
impl PcmSnapshot {
    pub fn samples(&self) -> &[f32] {
        &self.storage.samples[..self.storage.len]
    }
    pub fn key(&self) -> JobIdentity {
        self.storage.key
    }
    pub fn range(&self) -> SampleRange {
        self.storage.range
    }
}
/// Four shared immutable leases cover one in-flight job, two final jobs and one
/// partial job. Busy slots are never overwritten, even after rolling reset/wrap.
pub struct SnapshotPool {
    slots: Vec<Arc<Storage>>,
    max_samples: usize,
}
impl SnapshotPool {
    pub fn new(slots: usize, max_samples: usize) -> Result<Self, AudioError> {
        if slots == 0
            || slots > MAX_SNAPSHOT_SLOTS
            || max_samples == 0
            || max_samples > MAX_SNAPSHOT_SAMPLES
        {
            return Err(AudioError::InvalidCapacity);
        }
        let empty = JobIdentity {
            audio: AudioIdentity {
                session_id: 0,
                epoch: 0,
            },
            segment_id: 0,
            source_revision: 0,
        };
        Ok(Self {
            slots: (0..slots)
                .map(|_| {
                    Arc::new(Storage {
                        samples: vec![0.0; max_samples].into_boxed_slice(),
                        len: 0,
                        key: empty,
                        range: SampleRange { start: 0, end: 0 },
                    })
                })
                .collect(),
            max_samples,
        })
    }
    pub fn pcm_capacity_bytes(&self) -> usize {
        self.slots.len() * self.max_samples * std::mem::size_of::<f32>()
    }
    pub fn snapshot(
        &mut self,
        ring: &RollingAudio,
        key: JobIdentity,
        range: SampleRange,
    ) -> Result<PcmSnapshot, AudioError> {
        if key.audio != ring.identity {
            return Err(AudioError::StaleIdentity);
        }
        let count = range
            .end
            .checked_sub(range.start)
            .ok_or(AudioError::InvalidRange)?;
        if count == 0 {
            return Err(AudioError::InvalidRange);
        }
        if count > self.max_samples as u64 {
            return Err(AudioError::ResourceExhausted);
        }
        for slot in &mut self.slots {
            if let Some(storage) = Arc::get_mut(slot) {
                ring.copy(range, &mut storage.samples[..count as usize])?;
                storage.len = count as usize;
                storage.key = key;
                storage.range = range;
                return Ok(PcmSnapshot {
                    storage: Arc::clone(slot),
                });
            }
        }
        Err(AudioError::ResourceExhausted)
    }
}

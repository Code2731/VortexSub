use echosub_audio_core::*;
use std::f64::consts::TAU;
#[test]
fn native_anchor_only_moves_an_empty_matching_epoch_forward() {
    let id = identity(1);
    let mut ring = RollingAudio::new(16000, id, 100).unwrap();
    assert!(ring.anchor_empty(identity(2), 200).is_err());
    assert!(ring.anchor_empty(id, 99).is_err());
    ring.anchor_empty(id, 200).unwrap();
    ring.append(id, 200, &[0.2; 512]).unwrap();
    assert!(ring.anchor_empty(id, 900).is_err());
    ring.reset(identity(2), 16000).unwrap();
    ring.anchor_empty(identity(2), 17000).unwrap();
    assert_eq!(
        ring.retained_range(),
        SampleRange {
            start: 17000,
            end: 17000
        }
    );
}
fn identity(epoch: u64) -> AudioIdentity {
    AudioIdentity {
        session_id: 7,
        epoch,
    }
}
fn key(epoch: u64) -> JobIdentity {
    JobIdentity {
        audio: identity(epoch),
        segment_id: 9,
        source_revision: 3,
    }
}
fn format(rate: u32) -> AudioFormat {
    AudioFormat::new(rate, 2, Some(3)).unwrap()
}
fn stereo_sine(rate: u32, hz: f64, frames: usize) -> Vec<f32> {
    (0..frames)
        .flat_map(|n| {
            let s = (0.5 * (TAU * hz * n as f64 / rate as f64).sin()) as f32;
            [s, s]
        })
        .collect()
}
fn collect(batch: AudioBatch, output: &mut Vec<f32>, ranges: &mut Vec<SampleRange>) {
    for frame in batch.frames {
        assert_eq!(frame.range.end - frame.range.start, 512);
        ranges.push(frame.range);
        output.extend(frame.samples);
    }
    if let Some(tail) = batch.tail {
        assert!(tail.samples.len() < 512);
        assert_eq!(tail.range.end - tail.range.start, tail.samples.len() as u64);
        ranges.push(tail.range);
        output.extend(tail.samples);
    }
}
fn normalize(rate: u32, pcm: &[f32], packet_frames: usize) -> (Vec<f32>, Vec<SampleRange>) {
    let mut core = StreamNormalizer::new(format(rate), identity(1), 8000);
    let mut output = Vec::new();
    let mut ranges = Vec::new();
    for packet in pcm.chunks(packet_frames * 2) {
        collect(core.push(packet).unwrap(), &mut output, &mut ranges);
    }
    collect(core.finish().unwrap(), &mut output, &mut ranges);
    (output, ranges)
}
fn rms(samples: &[f32]) -> f64 {
    (samples.iter().map(|x| (*x as f64).powi(2)).sum::<f64>() / samples.len() as f64).sqrt()
}

#[test]
fn ut001_exact_count_duration_and_contiguous_ranges() {
    for rate in [44100, 48000] {
        let pcm = stereo_sine(rate, 1000.0, rate as usize);
        let (samples, ranges) = normalize(rate, &pcm, 137);
        assert_eq!(samples.len(), 16000);
        assert_eq!(ranges.first().unwrap().start, 8000);
        assert_eq!(ranges.last().unwrap().end, 24000);
        assert_eq!(
            ranges.last().unwrap().end_s() - ranges.first().unwrap().start_s(),
            1.0
        );
        for pair in ranges.windows(2) {
            assert_eq!(pair[0].end, pair[1].start);
        }
        let gain = rms(&samples[200..15800]) / rms(&pcm[400..pcm.len() - 400]);
        assert!((gain - 1.0).abs() < 0.005, "{rate}: passband gain {gain}");
    }
}
#[test]
fn ut001_packet_partition_does_not_change_samples_or_clock() {
    for rate in [44100, 48000] {
        let pcm = stereo_sine(rate, 1379.0, rate as usize + 913);
        let expected = normalize(rate, &pcm, rate as usize * 2);
        for packet in [1, 7, 113, 511, 4096] {
            assert_eq!(normalize(rate, &pcm, packet), expected);
        }
        assert_eq!(expected.0.len(), (pcm.len() / 2 * 16000) / rate as usize);
    }
}
#[test]
fn ut001_alias_rejection_above_output_nyquist() {
    for rate in [44100, 48000] {
        let pass = normalize(rate, &stereo_sine(rate, 1000.0, rate as usize), 211).0;
        for hz in [8100.0, 8500.0, 10000.0, 12000.0, 15000.0] {
            let stop = normalize(rate, &stereo_sine(rate, hz, rate as usize), 211).0;
            let ratio = rms(&stop[200..15800]) / rms(&pass[200..15800]);
            assert!(
                ratio < 0.001,
                "{rate}/{hz}: alias ratio {ratio}, dB {}",
                20.0 * ratio.log10()
            );
        }
    }
}
#[test]
fn ut001_stereo_downmix_dc_gain_and_clipping() {
    for rate in [44100, 48000] {
        let pcm: Vec<_> = (0..rate).flat_map(|_| [0.75, -0.25]).collect();
        let out = normalize(rate, &pcm, 431).0;
        assert!(out[200..15800].iter().all(|x| (*x - 0.25).abs() < 1e-6));
    }
    let mut core = StreamNormalizer::new(format(16000), identity(1), 0);
    let out = core.push(&vec![f32::MAX; 1024]).unwrap();
    assert!(out.frames[0].samples.iter().all(|x| *x == 1.0));
    assert_eq!(core.clipped_values(), 512);
}
#[test]
fn ut003_frames_and_unpadded_tail_preserve_real_duration() {
    let mono = AudioFormat::new(16000, 1, None).unwrap();
    let mut core = StreamNormalizer::new(mono, identity(1), 16000);
    assert!(core.push(&vec![0.25; 511]).unwrap().frames.is_empty());
    let batch = core.push(&[0.5, 0.75]).unwrap();
    assert_eq!(batch.frames.len(), 1);
    assert_eq!(
        batch.frames[0].range,
        SampleRange::new(16000, 16512).unwrap()
    );
    assert_eq!(batch.frames[0].samples[511], 0.5);
    let end = core.finish().unwrap();
    let tail = end.tail.unwrap();
    assert_eq!(tail.samples, vec![0.75]);
    assert_eq!(tail.range, SampleRange::new(16512, 16513).unwrap());
    assert!(matches!(core.push(&[0.0]), Err(AudioError::ClosedStream)));
    assert!(matches!(core.finish(), Err(AudioError::ClosedStream)));
}
#[test]
fn ut003_digital_silence_stays_exact_zero() {
    for rate in [44100, 48000] {
        let output = normalize(rate, &vec![0.0; rate as usize * 2], 97).0;
        assert_eq!(output.len(), 16000);
        assert!(output.iter().all(|x| *x == 0.0));
    }
}
#[test]
fn format_and_packet_rejection_are_transactional() {
    for bad in [
        (96000, 2, Some(3)),
        (48000, 6, Some(63)),
        (48000, 2, Some(12)),
        (48000, 1, Some(3)),
    ] {
        assert_eq!(
            AudioFormat::new(bad.0, bad.1, bad.2),
            Err(AudioError::UnsupportedFormat)
        );
    }
    let mut core = StreamNormalizer::new(format(48000), identity(1), 0);
    assert!(matches!(core.push(&[0.0]), Err(AudioError::InvalidPacket)));
    assert!(matches!(
        core.push(&[0.0, f32::NAN]),
        Err(AudioError::NonFiniteSample)
    ));
    assert!(matches!(
        core.push(&vec![0.0; 48000 * 4 + 2]),
        Err(AudioError::InvalidPacket)
    ));
    assert_eq!(core.input_end_sample().unwrap(), 0);
    assert_eq!(session_sample_from_ns(1_000_000_000).unwrap(), 16000);
    let mut overflowing = StreamNormalizer::new(format(16000), identity(1), u64::MAX);
    assert!(matches!(
        overflowing.push(&[0.0, 0.0]),
        Err(AudioError::TimestampOverflow)
    ));
    assert_eq!(overflowing.input_end_sample().unwrap(), u64::MAX);
}
#[test]
fn ut002_device_change_preserves_session_time_and_resets_filter() {
    let mut core = StreamNormalizer::new(format(48000), identity(1), 0);
    let old = core.push(&vec![0.5; 96000]).unwrap();
    let old_end = old.frames.last().unwrap().range.end;
    let gap = core
        .reanchor(format(44100), identity(2), 32000, GapReason::DeviceChange)
        .unwrap();
    assert_eq!(gap.range, SampleRange::new(old_end, 32000).unwrap());
    assert_eq!(gap.old_identity, identity(1));
    assert_eq!(gap.new_identity, identity(2));
    let fresh = core.push(&vec![0.0; 88200]).unwrap();
    assert_eq!(fresh.frames[0].range.start, 32000);
    assert!(fresh
        .frames
        .iter()
        .all(|f| f.identity == identity(2) && f.samples.iter().all(|x| *x == 0.0)));
    assert!(matches!(
        core.reanchor(format(48000), identity(3), 33000, GapReason::Pause),
        Err(AudioError::NonContiguous)
    ));
    assert!(matches!(
        core.reanchor(format(48000), identity(2), 64000, GapReason::Pause),
        Err(AudioError::StaleIdentity)
    ));
}
#[test]
fn ut008_rolling_wrap_has_fixed_capacity_and_exact_retained_range() {
    let mut ring = RollingAudio::new(4, identity(1), 100).unwrap();
    ring.append(identity(1), 100, &[0.1, 0.2, 0.3]).unwrap();
    ring.append(identity(1), 103, &[0.4, 0.5, 0.6]).unwrap();
    assert_eq!(ring.retained_range(), SampleRange::new(102, 106).unwrap());
    assert_eq!(ring.capacity(), 4);
    let mut pool = SnapshotPool::new(1, 4).unwrap();
    let copy = pool.snapshot(&ring, key(1), ring.retained_range()).unwrap();
    assert_eq!(copy.samples(), &[0.3, 0.4, 0.5, 0.6]);
    assert_eq!(copy.key(), key(1));
}
#[test]
fn ut008_pcm_lease_survives_wrap_reset_and_pool_drop() {
    let mut ring = RollingAudio::new(4, identity(1), 0).unwrap();
    ring.append(identity(1), 0, &[0.1, 0.2, 0.3, 0.4]).unwrap();
    let mut pool = SnapshotPool::new(2, 4).unwrap();
    let old = pool.snapshot(&ring, key(1), ring.retained_range()).unwrap();
    ring.append(identity(1), 4, &[0.5; 4]).unwrap();
    ring.reset(identity(2), 16000).unwrap();
    ring.append(identity(2), 16000, &[0.9; 4]).unwrap();
    let new = pool.snapshot(&ring, key(2), ring.retained_range()).unwrap();
    assert_eq!(old.samples(), &[0.1, 0.2, 0.3, 0.4]);
    assert_eq!(old.key(), key(1));
    assert_eq!(new.samples(), &[0.9; 4]);
    assert_eq!(new.key(), key(2));
    drop(pool);
    std::thread::spawn(move || {
        assert_eq!(old.samples(), &[0.1, 0.2, 0.3, 0.4]);
        assert_eq!(new.range().start, 16000);
    })
    .join()
    .unwrap();
}
#[test]
fn ut008_lease_clones_keep_slot_busy_and_exhaustion_is_explicit() {
    let mut ring = RollingAudio::new(4, identity(1), 0).unwrap();
    ring.append(identity(1), 0, &[0.1; 4]).unwrap();
    let mut pool = SnapshotPool::new(1, 4).unwrap();
    let one = pool.snapshot(&ring, key(1), ring.retained_range()).unwrap();
    let clone = one.clone();
    drop(one);
    assert!(matches!(
        pool.snapshot(&ring, key(1), ring.retained_range()),
        Err(AudioError::ResourceExhausted)
    ));
    drop(clone);
    assert!(pool.snapshot(&ring, key(1), ring.retained_range()).is_ok());
}
#[test]
fn ut008_reject_stale_or_overwritten_audio_and_enforce_pcm_budget() {
    let mut ring = RollingAudio::new(4, identity(1), 0).unwrap();
    ring.append(identity(1), 0, &[0.2; 8]).unwrap();
    let mut pool = SnapshotPool::new(1, 4).unwrap();
    assert!(matches!(
        pool.snapshot(&ring, key(1), SampleRange::new(0, 4).unwrap()),
        Err(AudioError::RangeUnavailable)
    ));
    assert!(matches!(
        pool.snapshot(&ring, key(2), ring.retained_range()),
        Err(AudioError::StaleIdentity)
    ));
    assert!(matches!(
        ring.append(identity(2), 8, &[0.0]),
        Err(AudioError::StaleIdentity)
    ));
    assert!(matches!(
        ring.append(identity(1), 9, &[0.0]),
        Err(AudioError::NonContiguous)
    ));
    assert_eq!(ring.retained_range(), SampleRange::new(4, 8).unwrap());
    let max = SnapshotPool::new(4, MAX_SNAPSHOT_SAMPLES).unwrap();
    assert_eq!(max.pcm_capacity_bytes(), 2_048_000);
    assert!(matches!(
        SnapshotPool::new(5, 1),
        Err(AudioError::InvalidCapacity)
    ));
    assert!(matches!(
        SnapshotPool::new(1, MAX_SNAPSHOT_SAMPLES + 1),
        Err(AudioError::InvalidCapacity)
    ));
    assert!(matches!(
        RollingAudio::new(MAX_ROLLING_SAMPLES + 1, identity(1), 0),
        Err(AudioError::InvalidCapacity)
    ));
}

#[test]
fn ut001_filter_center_timestamps_do_not_include_delivery_lookahead() {
    for rate in [44100, 48000] {
        let mut pcm = vec![0.0; rate as usize * 2];
        let input_peak = rate as usize / 10;
        pcm[input_peak * 2] = 0.5;
        pcm[input_peak * 2 + 1] = 0.5;
        let (output, ranges) = normalize(rate, &pcm, 29);
        let peak = output
            .iter()
            .enumerate()
            .max_by(|a, b| a.1.abs().total_cmp(&b.1.abs()))
            .unwrap()
            .0;
        assert_eq!(peak, 1600);
        assert_eq!(ranges[0].start + peak as u64, 9600);
        let core = StreamNormalizer::new(format(rate), identity(1), 8000);
        assert!((core.look_ahead_s() - 128.0 / rate as f64).abs() < 1e-12);
    }
}
#[test]
fn ut002_ring_reanchor_rejects_backward_time_or_reused_epoch() {
    let mut ring = RollingAudio::new(8, identity(1), 100).unwrap();
    ring.append(identity(1), 100, &[0.1; 8]).unwrap();
    assert_eq!(ring.reset(identity(2), 107), Err(AudioError::NonContiguous));
    assert_eq!(ring.reset(identity(1), 200), Err(AudioError::StaleIdentity));
    assert_eq!(ring.retained_range(), SampleRange::new(100, 108).unwrap());
    ring.reset(identity(2), 200).unwrap();
    assert_eq!(
        ring.retained_range(),
        SampleRange {
            start: 200,
            end: 200
        }
    );
    assert_eq!(
        ring.append(identity(2), 200, &[1.1]),
        Err(AudioError::InvalidAmplitude)
    );
    assert_eq!(
        ring.retained_range(),
        SampleRange {
            start: 200,
            end: 200
        }
    );
}

#[test]
fn ut001_mono_matches_identical_stereo_channels() {
    for rate in [16000, 44100, 48000] {
        let stereo = stereo_sine(rate, 2137.0, rate as usize);
        let expected = normalize(rate, &stereo, 157).0;
        let mono: Vec<_> = stereo.chunks_exact(2).map(|f| f[0]).collect();
        let mut core = StreamNormalizer::new(
            AudioFormat::new(rate, 1, Some(4)).unwrap(),
            identity(1),
            8000,
        );
        let mut output = Vec::new();
        let mut ranges = Vec::new();
        for packet in mono.chunks(157) {
            collect(core.push(packet).unwrap(), &mut output, &mut ranges);
        }
        collect(core.finish().unwrap(), &mut output, &mut ranges);
        assert_eq!(output, expected);
    }
}

use echosub_audio_core::*;

const ID: AudioIdentity = AudioIdentity {
    session_id: 7,
    epoch: 1,
};
fn ns(sample: u64) -> u64 {
    sample * 1_000_000_000 / SAMPLE_RATE as u64
}
fn frame(start: u64, value: f32) -> AudioFrame {
    AudioFrame {
        identity: ID,
        range: SampleRange {
            start,
            end: start + 512,
        },
        samples: [value; 512],
    }
}
fn core() -> VadSegmenter {
    VadSegmenter::new(ID, 0, VadSettings::default()).unwrap()
}
fn feed(v: &mut VadSegmenter, count: usize, voiced: bool) -> Vec<SpeechEvent> {
    let mut out = Vec::new();
    for _ in 0..count {
        let f = frame(v.cursor(), if voiced { 0.25 } else { 0.0 });
        out.extend(
            v.push(&f, if voiced { Some(0.9) } else { None }, ns(f.range.end))
                .unwrap(),
        );
    }
    out
}
fn finals(events: &[SpeechEvent]) -> Vec<(SegmentInfo, FinalReason)> {
    events
        .iter()
        .filter_map(|e| match e {
            SpeechEvent::Final { segment, reason } => Some((*segment, *reason)),
            _ => None,
        })
        .collect()
}

#[test]
fn settings_keep_requested_and_effective_seconds() {
    let v = core();
    assert_eq!(v.requested_settings().pre_roll_s, 0.3);
    let e = v.effective_settings();
    assert_eq!(
        (e.pre_roll_samples, e.post_roll_samples, e.overlap_samples),
        (4608, 3072, 9728)
    );
    assert_eq!(e.partial_interval_samples, 15872);
    for bad in [
        VadSettings {
            max_chunk_s: 8.1,
            ..Default::default()
        },
        VadSettings {
            speech_threshold: f32::NAN,
            ..Default::default()
        },
        VadSettings {
            partial_minimum_speech_s: 0.032,
            ..Default::default()
        },
        VadSettings {
            post_roll_s: 0.6,
            ..Default::default()
        },
    ] {
        assert_eq!(bad.effective(), Err(AudioError::InvalidVadConfig));
    }
}

#[test]
fn ten_minutes_digital_silence_needs_no_model_or_asr_requests() {
    let mut v = core();
    let mut model_calls = 0;
    let mut asr_requests = 0;
    for _ in 0..18_750 {
        let f = frame(v.cursor(), 0.0);
        let p = if VadSegmenter::requires_probability(&f.samples) {
            model_calls += 1;
            Some(1.0)
        } else {
            None
        };
        let events = v.push(&f, p, ns(f.range.end)).unwrap();
        asr_requests += events
            .iter()
            .filter(|e| matches!(e, SpeechEvent::Partial(_) | SpeechEvent::Final { .. }))
            .count();
    }
    assert_eq!((model_calls, asr_requests, v.cursor()), (0, 0, 9_600_000));
    assert!(v.active_segment().is_none());
    // Even a stale positive probability cannot turn exact zero into speech.
    assert!(v
        .push(&frame(v.cursor(), 0.0), Some(1.0), ns(v.cursor() + 512))
        .unwrap()
        .is_empty());
}

#[test]
fn silence_final_has_pre_post_roll_and_exact_minimum() {
    let mut v = core();
    feed(&mut v, 20, false);
    feed(&mut v, 5, true);
    assert!(finals(&feed(&mut v, 14, false)).is_empty());
    let result = finals(&feed(&mut v, 1, false));
    assert_eq!(result.len(), 1);
    let (s, why) = result[0];
    assert_eq!(why, FinalReason::Silence);
    assert_eq!(
        s.pcm_range,
        SampleRange {
            start: 11 * 512,
            end: 31 * 512
        }
    );
    assert_eq!(
        s.voice_range,
        SampleRange {
            start: 20 * 512,
            end: 25 * 512
        }
    );
    assert_eq!(s.voiced_samples, 2560);
    assert!(v.active_segment().is_none());
}

#[test]
fn short_speech_is_discarded_without_partial() {
    let mut v = core();
    let mut events = feed(&mut v, 4, true);
    events.extend(feed(&mut v, 15, false));
    assert!(finals(&events).is_empty());
    assert!(!events.iter().any(|e| matches!(e, SpeechEvent::Partial(_))));
    assert!(events.iter().any(|e| matches!(
        e,
        SpeechEvent::Discarded {
            reason: DiscardReason::TooShort,
            ..
        }
    )));
}

#[test]
fn partial_requests_are_thresholded_and_rate_limited() {
    let mut v = core();
    let events = feed(&mut v, 90, true);
    let ends: Vec<_> = events
        .iter()
        .filter_map(|e| match e {
            SpeechEvent::Partial(s) => Some(s.pcm_range.end),
            _ => None,
        })
        .collect();
    assert_eq!(ends, vec![25 * 512, 56 * 512, 87 * 512]);
    assert!(finals(&events).is_empty());
}

#[test]
fn long_speech_chunks_include_overlap_without_losing_new_samples() {
    let mut v = core();
    let mut events = feed(&mut v, 540, true);
    events.extend(v.close(ns(v.cursor())).unwrap());
    let result = finals(&events);
    assert_eq!(result.len(), 3);
    assert_eq!(
        result[0].0.pcm_range.end - result[0].0.pcm_range.start,
        128000
    );
    let mut new_voice = 0;
    for (i, (s, _)) in result.iter().enumerate() {
        assert!(s.pcm_range.end - s.pcm_range.start <= 128000);
        new_voice += s.new_voiced_samples;
        if i > 0 {
            let prev = result[i - 1].0;
            assert_eq!(s.continued_from, Some(prev.id));
            assert_eq!(s.pcm_range.start, prev.pcm_range.end - 9728);
            assert_eq!(s.id.segment_id, prev.id.segment_id + 1);
        }
    }
    assert_eq!(new_voice, 540 * 512);
    assert_eq!(result.last().unwrap().1, FinalReason::EndOfStream);
}

#[test]
fn overlap_alone_does_not_create_an_extra_final() {
    let mut v = core();
    let mut events = feed(&mut v, 250, true);
    events.extend(feed(&mut v, 15, false));
    events.extend(v.close(ns(v.cursor())).unwrap());
    assert_eq!(finals(&events).len(), 1);
    let mut v = core();
    let mut events = feed(&mut v, 250, true);
    events.extend(
        v.poll(ns(v.cursor()) + 480_000_000, CaptureHealth::Healthy)
            .unwrap(),
    );
    events.extend(v.close(ns(v.cursor()) + 480_000_000).unwrap());
    assert_eq!(finals(&events).len(), 1);
}

#[test]
fn watchdog_counts_known_silence_but_never_appends_missing_pcm() {
    let mut v = core();
    feed(&mut v, 5, true);
    feed(&mut v, 3, false);
    let cursor = v.cursor();
    assert!(v
        .poll(ns(cursor) + 383_000_000, CaptureHealth::Healthy)
        .unwrap()
        .is_empty());
    let result = finals(
        &v.poll(ns(cursor) + 384_000_000, CaptureHealth::Healthy)
            .unwrap(),
    );
    assert_eq!(result.len(), 1);
    assert_eq!(result[0].1, FinalReason::PacketStopped);
    assert_eq!(result[0].0.pcm_range.end, cursor);
    assert_eq!(v.cursor(), cursor);
    assert!(v
        .poll(ns(cursor) + 1_000_000_000, CaptureHealth::Healthy)
        .unwrap()
        .is_empty());
}

#[test]
fn interruptions_discard_even_qualified_speech_and_close_stream() {
    for reason in [
        DiscardReason::Pause,
        DiscardReason::Stop,
        DiscardReason::CaptureFault,
    ] {
        let mut v = core();
        feed(&mut v, 30, true);
        let now = ns(v.cursor()) + 1_000_000_000;
        let events = if reason == DiscardReason::CaptureFault {
            v.poll(now, CaptureHealth::Faulted).unwrap()
        } else {
            v.interrupt(reason, now).unwrap()
        };
        assert!(finals(&events).is_empty());
        assert!(events
            .iter()
            .any(|e| matches!(e, SpeechEvent::Discarded { reason: r, .. } if *r == reason)));
        assert_eq!(
            v.poll(now, CaptureHealth::Healthy),
            Err(AudioError::ClosedStream)
        );
    }
}

#[test]
fn epoch_gap_discards_old_state_and_rejects_old_frames() {
    let mut v = core();
    feed(&mut v, 5, true);
    let new = AudioIdentity { epoch: 2, ..ID };
    let gap = AudioGap {
        old_identity: ID,
        new_identity: new,
        range: SampleRange {
            start: v.cursor(),
            end: 16000,
        },
        reason: GapReason::DeviceChange,
    };
    let events = v.reset(gap, ns(16000)).unwrap();
    assert!(finals(&events).is_empty());
    assert!(events.contains(&SpeechEvent::ModelReset(VadResetReason::Gap)));
    assert_eq!(
        v.push(&frame(16000, 0.25), Some(0.9), ns(16512)),
        Err(AudioError::StaleIdentity)
    );
    let mut f = frame(16000, 0.25);
    f.identity = new;
    v.push(&f, Some(0.9), ns(16512)).unwrap();
    let s = v.active_segment().unwrap();
    assert_eq!(s.id.audio, new);
    assert_eq!(s.id.segment_id, 2);
    assert_eq!(s.pcm_range.start, 16000);
}

#[test]
fn invalid_input_is_transactional_and_clock_must_be_monotonic() {
    let mut v = core();
    feed(&mut v, 5, true);
    let before = v.active_segment();
    let f = frame(v.cursor(), 0.25);
    for p in [None, Some(f32::NAN), Some(1.1)] {
        assert_eq!(
            v.push(&f, p, ns(f.range.end)),
            Err(AudioError::InvalidProbability)
        );
        assert_eq!(v.active_segment(), before);
        assert_eq!(v.cursor(), f.range.start);
    }
    assert_eq!(v.push(&f, Some(0.9), 0), Err(AudioError::ClockRegression));
    let mut bad = frame(f.range.start, 0.25);
    bad.samples[3] = f32::INFINITY;
    assert_eq!(
        v.push(&bad, Some(0.9), ns(f.range.end)),
        Err(AudioError::NonFiniteSample)
    );
    v.push(&f, Some(0.9), ns(f.range.end)).unwrap();
}

#[test]
fn tail_padding_cannot_qualify_short_speech() {
    for (full, tail_len, qualifies) in [(4, 511, false), (5, 100, true)] {
        let mut v = core();
        feed(&mut v, full, true);
        let end = v.cursor() + tail_len;
        let tail = AudioTail {
            identity: ID,
            range: SampleRange {
                start: v.cursor(),
                end,
            },
            samples: vec![0.25; tail_len as usize],
        };
        let events = v.close_with_tail(&tail, Some(0.9), ns(end)).unwrap();
        let result = finals(&events);
        assert_eq!(!result.is_empty(), qualifies);
        if qualifies {
            assert_eq!(result[0].0.voiced_samples, end);
            assert_eq!(result[0].0.pcm_range.end, end);
        }
        assert_eq!(v.cursor(), end);
    }
}

#[test]
fn normalization_segmentation_and_snapshot_share_actual_sample_ranges() {
    let mut n = StreamNormalizer::new(AudioFormat::new(16000, 1, None).unwrap(), ID, 0);
    let mut ring = RollingAudio::new(MAX_ROLLING_SAMPLES, ID, 0).unwrap();
    let mut pool = SnapshotPool::new(4, MAX_SNAPSHOT_SAMPLES).unwrap();
    let mut v = core();
    let mut held = None;
    for i in 0..400 {
        let batch = n.push(&[0.25; 512]).unwrap();
        for f in batch.frames {
            ring.append(ID, f.range.start, &f.samples).unwrap();
            let events = v.push(&f, Some(0.9), ns(f.range.end)).unwrap();
            for (s, _) in finals(&events) {
                let key = JobIdentity {
                    audio: s.id.audio,
                    segment_id: s.id.segment_id,
                    source_revision: 1,
                };
                let snapshot = pool.snapshot(&ring, key, s.pcm_range).unwrap();
                assert_eq!(
                    snapshot.samples().len() as u64,
                    s.pcm_range.end - s.pcm_range.start
                );
                if held.is_none() {
                    held = Some(snapshot);
                }
            }
        }
        assert_eq!(v.cursor(), (i + 1) * 512);
    }
    assert!(ring.retained_range().start > 0);
    let held = held.unwrap();
    assert_eq!(
        held.range(),
        SampleRange {
            start: 0,
            end: 128000
        }
    );
    assert!(held.samples().iter().all(|x| *x == 0.25));
}

#[test]
fn nonzero_non_speech_does_not_request_asr_and_reset_is_explicit() {
    let mut v = core();
    for _ in 0..100 {
        let f = frame(v.cursor(), 0.25);
        assert!(v.push(&f, Some(0.1), ns(f.range.end)).unwrap().is_empty());
    }
    let events = feed(&mut v, 1, false);
    assert_eq!(
        events,
        vec![SpeechEvent::ModelReset(VadResetReason::DigitalSilence)]
    );
    assert!(feed(&mut v, 1, false).is_empty());
}

#[test]
fn pre_roll_counts_toward_chunk_budget_and_short_new_suffix_is_preserved() {
    let mut v = core();
    feed(&mut v, 20, false);
    let mut events = feed(&mut v, 242, true);
    events.extend(v.close(ns(v.cursor())).unwrap());
    let result = finals(&events);
    assert_eq!(result.len(), 2);
    assert_eq!(
        result[0].0.pcm_range.end - result[0].0.pcm_range.start,
        128000
    );
    assert_eq!(result[1].0.new_voiced_samples, 512);
    assert_eq!(
        result
            .iter()
            .map(|(s, _)| s.new_voiced_samples)
            .sum::<u64>(),
        242 * 512
    );
}

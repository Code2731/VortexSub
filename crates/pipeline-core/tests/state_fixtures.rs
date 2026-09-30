use echosub_audio_core::*;
use echosub_pipeline_core::*;
const ID: AudioIdentity = AudioIdentity {
    session_id: 1,
    epoch: 1,
};
#[test]
fn no_speech_and_overlap_only_skip_final_without_translation_and_preserve_partial_text() {
    for (outcome, reason) in [
        (Outcome::NoSpeech, Reason::NoSpeech),
        (Outcome::OverlapOnly, Reason::OverlapOnly),
    ] {
        let mut f = Fixture::new(1000, "ko");
        f.submit(1, AsrKind::Final, 512, 0);
        let job = f.core.next_asr().unwrap();
        f.core.complete_asr(job.key(), outcome, 0).unwrap();
        let record = &f.records()[0];
        assert_eq!(record.source_state, SourceState::Skipped);
        assert_eq!(record.source_reason, Some(reason));
        assert!(record.source.is_empty());
        assert!(f.core.next_translation(0).unwrap().is_none());
    }
    let mut f = Fixture::new(1000, "ko");
    f.submit(1, AsrKind::Partial, 512, 0);
    let job = f.core.next_asr().unwrap();
    f.core
        .complete_asr(job.key(), Outcome::Text("실제 원문".into()), 0)
        .unwrap();
    drop(job);
    f.submit(1, AsrKind::Partial, 1024, 1);
    let job = f.core.next_asr().unwrap();
    f.core
        .complete_asr(job.key(), Outcome::NoSpeech, 1)
        .unwrap();
    assert_eq!(f.records()[0].source, "실제 원문");
    assert_eq!(f.records()[0].applied_source_revision, Some(1));
    assert_eq!(f.records()[0].source_reason, Some(Reason::NoSpeech));
}
#[test]
fn asr_only_final_does_not_schedule_or_fake_translation() {
    let mut f = Fixture::new(1000, "ko");
    f.core = Pipeline::new_asr_only(ID, 1000).unwrap();
    f.finish(1, "실제 원문", 0);
    assert!(f.core.next_translation(0).unwrap().is_none());
    assert_eq!(f.records()[0].translation_state, TranslationState::None);
    assert!(f.records()[0].translation.is_empty());
}
#[test]
fn translation_reconfiguration_waits_for_reservations_and_language_is_job_local() {
    let mut f = Fixture::new(1000, "ko");
    f.finish(1, "first", 0);
    assert_eq!(
        f.core.set_translation_enabled(false),
        Err(CoreError::InvalidConfig)
    );
    f.core.set_translation_languages("ja", "ko").unwrap();
    let first = f.core.next_translation(0).unwrap().unwrap();
    assert_eq!(first.source_language, "en");
    assert_eq!(
        f.core.set_translation_enabled(false),
        Err(CoreError::InvalidConfig)
    );
    f.core
        .complete_translation(first.key, Outcome::Text("첫째".into()), 1)
        .unwrap();
    f.finish(2, "次", 2);
    let next = f.core.next_translation(2).unwrap().unwrap();
    assert_eq!(next.source_language, "ja");
    f.core
        .complete_translation(next.key, Outcome::Text("다음".into()), 3)
        .unwrap();
    f.core.set_translation_enabled(false).unwrap();
    f.finish(3, "source only", 4);
    assert_eq!(f.records()[2].translation_state, TranslationState::None);
}
#[test]
fn invalid_translation_language_does_not_change_bypass_policy() {
    let mut f = Fixture::new(1000, "ko");
    f.core.set_translation_languages("ko", "ko").unwrap();
    assert_eq!(
        f.core.set_translation_languages("auto", "ko"),
        Err(CoreError::InvalidConfig)
    );
    f.finish(1, "한국어", 0);
    assert_eq!(f.records()[0].translation_state, TranslationState::Bypassed);
    assert!(f.core.next_translation(0).unwrap().is_none());
}
struct Fixture {
    core: Pipeline,
    ring: RollingAudio,
    pool: SnapshotPool,
}
impl Fixture {
    fn new(cap: usize, target: &str) -> Self {
        let mut ring = RollingAudio::new(MAX_ROLLING_SAMPLES, ID, 0).unwrap();
        ring.append(ID, 0, &vec![0.25; MAX_ROLLING_SAMPLES])
            .unwrap();
        Self {
            core: Pipeline::new(ID, cap, "en", target).unwrap(),
            ring,
            pool: SnapshotPool::new(4, MAX_SNAPSHOT_SAMPLES).unwrap(),
        }
    }
    fn submit(&mut self, segment: u64, kind: AsrKind, end: u64, now: u64) -> Admission {
        self.core
            .submit_asr(
                SegmentIdentity {
                    audio: ID,
                    segment_id: segment,
                },
                SampleRange { start: 0, end },
                kind,
                &self.ring,
                &mut self.pool,
                now,
            )
            .unwrap()
    }
    fn finish(&mut self, segment: u64, text: &str, now: u64) -> JobIdentity {
        let admitted = self.submit(segment, AsrKind::Final, 512, now);
        assert!(admitted.queued);
        let job = self.core.next_asr().unwrap();
        assert_eq!(
            self.core
                .complete_asr(job.key(), Outcome::Text(text.into()), now)
                .unwrap(),
            Apply::Applied
        );
        admitted.key
    }
    fn records(&self) -> Vec<Record> {
        self.core
            .history_page(self.core.version(), 0, 100)
            .unwrap()
            .records
    }
}

#[test]
fn latest_partial_replaces_waiting_pcm_and_stale_result_is_ignored() {
    let mut f = Fixture::new(1000, "ko");
    let first = f.submit(1, AsrKind::Partial, 512, 0);
    let running = f.core.next_asr().unwrap();
    f.submit(1, AsrKind::Partial, 1024, 1);
    let latest = f.submit(1, AsrKind::Partial, 1536, 2);
    assert_eq!(f.core.queue_lengths(), (0, 1, 0));
    assert_eq!(latest.key.source_revision, 3);
    assert_eq!(
        f.core
            .complete_asr(first.key, Outcome::Text("old".into()), 3)
            .unwrap(),
        Apply::Ignored
    );
    drop(running);
    let job = f.core.next_asr().unwrap();
    assert_eq!(job.key(), latest.key);
    assert_eq!(job.pcm.range().end, 1536);
    f.core
        .complete_asr(job.key(), Outcome::Text("new".into()), 4)
        .unwrap();
    assert_eq!(f.records()[0].source, "new");
    assert_eq!(f.records()[0].applied_source_revision, Some(3));
    assert!(f.core.next_translation(4).unwrap().is_none());
}

#[test]
fn final_cancels_partial_but_waits_for_actual_return_and_freezes_source() {
    let mut f = Fixture::new(1000, "ko");
    f.submit(1, AsrKind::Partial, 1024, 0);
    let partial = f.core.next_asr().unwrap();
    let final_admission = f.submit(1, AsrKind::Final, 512, 1); // real post-roll may trim a partial range
    assert_eq!(final_admission.cancel_asr, Some(partial.key()));
    assert!(f.core.next_asr().is_none());
    assert_eq!(
        f.core
            .complete_asr(partial.key(), Outcome::Text("late".into()), 2)
            .unwrap(),
        Apply::Ignored
    );
    drop(partial);
    let final_job = f.core.next_asr().unwrap();
    assert_eq!(final_job.kind, AsrKind::Final);
    f.core
        .complete_asr(final_job.key(), Outcome::Text("안 돼, 안 돼\n🙂".into()), 3)
        .unwrap();
    assert_eq!(f.records()[0].source_state, SourceState::Final);
    let id = SegmentIdentity {
        audio: ID,
        segment_id: 1,
    };
    assert_eq!(
        f.core.submit_asr(
            id,
            SampleRange {
                start: 0,
                end: 1024
            },
            AsrKind::Partial,
            &f.ring,
            &mut f.pool,
            4
        ),
        Err(CoreError::Frozen)
    );
    assert_eq!(
        f.core
            .complete_asr(final_job.key(), Outcome::Text("duplicate".into()), 4),
        Err(CoreError::UnknownJob)
    );
}

#[test]
fn finals_are_fifo_and_take_priority_over_latest_partial() {
    let mut f = Fixture::new(1000, "en");
    f.submit(1, AsrKind::Final, 512, 0);
    f.submit(2, AsrKind::Final, 512, 0);
    f.submit(3, AsrKind::Partial, 512, 0);
    for n in 1..=3 {
        let job = f.core.next_asr().unwrap();
        assert_eq!(job.key().segment_id, n);
        f.core
            .complete_asr(job.key(), Outcome::Text(format!("{n}")), 0)
            .unwrap();
    }
}

#[test]
fn final_overflow_is_an_explicit_skipped_record() {
    let mut f = Fixture::new(1000, "ko");
    f.submit(1, AsrKind::Final, 512, 0);
    f.submit(2, AsrKind::Final, 512, 0);
    assert!(!f.submit(3, AsrKind::Final, 512, 0).queued);
    assert_eq!(f.core.queue_lengths(), (2, 0, 0));
    let skipped = &f.records()[2];
    assert_eq!(skipped.source_state, SourceState::Skipped);
    assert_eq!(skipped.source_reason, Some(Reason::QueueFull));
    assert_eq!(skipped.range, SampleRange { start: 0, end: 512 });
}

#[test]
fn replacing_partial_succeeds_with_all_four_pool_slots_in_use() {
    let mut f = Fixture::new(1000, "en");
    f.submit(1, AsrKind::Final, 512, 0);
    let running = f.core.next_asr().unwrap();
    f.submit(2, AsrKind::Final, 512, 0);
    f.submit(3, AsrKind::Final, 512, 0);
    f.submit(4, AsrKind::Partial, 512, 0);
    assert!(f.submit(4, AsrKind::Partial, 1024, 0).queued);
    assert_eq!(f.core.queue_lengths(), (2, 1, 0));
    assert!(running.pcm.samples().iter().all(|x| *x == 0.25));
}

#[test]
fn unavailable_or_externally_leased_pcm_is_not_silently_lost() {
    let mut f = Fixture::new(1000, "en");
    f.pool = SnapshotPool::new(1, MAX_SNAPSHOT_SAMPLES).unwrap();
    let lease = f
        .pool
        .snapshot(
            &f.ring,
            JobIdentity {
                audio: ID,
                segment_id: 99,
                source_revision: 1,
            },
            SampleRange { start: 0, end: 512 },
        )
        .unwrap();
    assert!(!f.submit(1, AsrKind::Final, 512, 0).queued);
    assert_eq!(
        f.records()[0].source_reason,
        Some(Reason::SnapshotUnavailable)
    );
    drop(lease);
    f.ring
        .append(ID, MAX_ROLLING_SAMPLES as u64, &[0.5; 512])
        .unwrap();
    assert!(!f.submit(2, AsrKind::Final, 512, 0).queued);
    assert_eq!(f.records()[1].source_state, SourceState::Skipped);
}

#[test]
fn queued_final_pcm_survives_ring_wrap_and_epoch_reset() {
    let mut f = Fixture::new(1000, "en");
    f.submit(1, AsrKind::Final, 512, 0);
    f.ring
        .append(
            ID,
            MAX_ROLLING_SAMPLES as u64,
            &vec![0.8; MAX_ROLLING_SAMPLES],
        )
        .unwrap();
    f.ring
        .reset(
            AudioIdentity { epoch: 2, ..ID },
            2 * MAX_ROLLING_SAMPLES as u64,
        )
        .unwrap();
    let job = f.core.next_asr().unwrap();
    assert_eq!(job.pcm.range(), SampleRange { start: 0, end: 512 });
    assert!(job.pcm.samples().iter().all(|x| *x == 0.25));
}

#[test]
fn displayed_partial_retains_its_applied_revision_while_new_work_waits() {
    let mut f = Fixture::new(1000, "ko");
    f.submit(1, AsrKind::Partial, 512, 0);
    let job = f.core.next_asr().unwrap();
    f.core
        .complete_asr(job.key(), Outcome::Text("visible".into()), 0)
        .unwrap();
    drop(job);
    f.submit(1, AsrKind::Partial, 1024, 1);
    let r = &f.records()[0];
    assert_eq!(r.key.source_revision, 2);
    assert_eq!(r.applied_source_revision, Some(1));
    assert_eq!(r.source, "visible");
}

#[test]
fn translation_is_final_only_single_flight_and_bounded() {
    let mut f = Fixture::new(1000, "ko");
    f.finish(1, "one", 0);
    let first = f.core.next_translation(0).unwrap().unwrap();
    f.finish(2, "two", 0);
    f.finish(3, "three", 0);
    f.finish(4, "four", 0);
    assert_eq!(f.core.queue_lengths().2, 2);
    assert!(f.core.next_translation(0).unwrap().is_none());
    assert_eq!(f.records()[3].translation_state, TranslationState::Skipped);
    assert_eq!(f.records()[3].translation_reason, Some(Reason::QueueFull));
    f.core
        .complete_translation(first.key, Outcome::Text("하나".into()), 1)
        .unwrap();
    assert_eq!(f.records()[0].translation_state, TranslationState::Done);
    assert_eq!(
        f.core
            .next_translation(1)
            .unwrap()
            .unwrap()
            .key
            .source
            .segment_id,
        2
    );
}

#[test]
fn translation_context_is_two_final_sources_from_same_epoch_and_keeps_repetition() {
    let mut f = Fixture::new(1000, "ko");
    for n in 1..=4 {
        f.finish(n, "No, no!", 0);
        let job = f.core.next_translation(0).unwrap().unwrap();
        assert_eq!(job.context.len(), (n as usize - 1).min(2));
        assert!(job.context.iter().all(|s| s == "No, no!"));
        f.core
            .complete_translation(job.key, Outcome::Text("안 돼, 안 돼!".into()), 0)
            .unwrap();
    }
    assert_eq!(f.records().len(), 4);
    let new = AudioIdentity { epoch: 2, ..ID };
    f.core.restart(new, false, 1).unwrap();
    f.ring.reset(new, MAX_ROLLING_SAMPLES as u64).unwrap();
    f.ring
        .append(new, MAX_ROLLING_SAMPLES as u64, &[0.25; 512])
        .unwrap();
    f.core
        .submit_asr(
            SegmentIdentity {
                audio: new,
                segment_id: 5,
            },
            SampleRange {
                start: MAX_ROLLING_SAMPLES as u64,
                end: MAX_ROLLING_SAMPLES as u64 + 512,
            },
            AsrKind::Final,
            &f.ring,
            &mut f.pool,
            1,
        )
        .unwrap();
    let asr = f.core.next_asr().unwrap();
    f.core
        .complete_asr(asr.key(), Outcome::Text("fresh".into()), 1)
        .unwrap();
    assert!(f
        .core
        .next_translation(1)
        .unwrap()
        .unwrap()
        .context
        .is_empty());
}

#[test]
fn same_language_bypasses_translation_without_dispatch() {
    let mut f = Fixture::new(1000, "EN");
    f.finish(1, "same", 0);
    assert_eq!(f.records()[0].translation_state, TranslationState::Bypassed);
    assert!(f.core.next_translation(0).unwrap().is_none());
}

#[test]
fn deadline_ends_queued_and_running_pending_but_waits_for_return() {
    let mut f = Fixture::new(1000, "ko");
    f.finish(1, "one", 0);
    let first = f.core.next_translation(0).unwrap().unwrap();
    f.finish(2, "two", 1);
    let cancel = f.core.poll(TRANSLATION_DEADLINE_NS + 1).unwrap();
    assert_eq!(cancel.translation, Some(first.key));
    assert!(f
        .records()
        .iter()
        .all(|r| r.translation_state == TranslationState::Skipped
            && r.source_state == SourceState::Final));
    f.finish(3, "three", TRANSLATION_DEADLINE_NS + 2);
    assert!(f
        .core
        .next_translation(TRANSLATION_DEADLINE_NS + 2)
        .unwrap()
        .is_none());
    assert_eq!(
        f.core
            .complete_translation(
                first.key,
                Outcome::Text("late".into()),
                TRANSLATION_DEADLINE_NS + 3
            )
            .unwrap(),
        Apply::Ignored
    );
    assert!(f
        .core
        .next_translation(TRANSLATION_DEADLINE_NS + 3)
        .unwrap()
        .is_some());
}

#[test]
fn expired_queue_entries_are_removed_before_new_final_translation_admission() {
    let mut f = Fixture::new(1000, "ko");
    f.finish(1, "one", 0);
    f.finish(2, "two", 0);
    f.finish(3, "three", TRANSLATION_DEADLINE_NS);
    assert_eq!(f.records()[2].translation_state, TranslationState::Pending);
    assert_eq!(f.core.queue_lengths().2, 1);
}

#[test]
fn pause_and_new_session_reject_old_results_and_terminalize_pending_records() {
    let mut f = Fixture::new(1000, "ko");
    f.finish(1, "keep source", 0);
    let tr = f.core.next_translation(0).unwrap().unwrap();
    f.submit(2, AsrKind::Final, 512, 0);
    let asr = f.core.next_asr().unwrap();
    f.core.interrupt(1).unwrap();
    assert_eq!(f.records()[0].source, "keep source");
    assert_eq!(f.records()[0].translation_state, TranslationState::Skipped);
    assert_eq!(f.records()[1].source_state, SourceState::Discarded);
    f.core
        .restart(
            AudioIdentity {
                session_id: 2,
                epoch: 1,
            },
            true,
            2,
        )
        .unwrap();
    assert!(f.records().is_empty());
    assert_eq!(
        f.core
            .complete_translation(tr.key, Outcome::Text("wrong".into()), 3)
            .unwrap(),
        Apply::Ignored
    );
    assert_eq!(
        f.core
            .complete_asr(asr.key(), Outcome::Text("wrong".into()), 3)
            .unwrap(),
        Apply::Ignored
    );
    assert!(f.records().is_empty());
}

#[test]
fn wrong_translation_request_cannot_release_or_modify_current_job() {
    let mut f = Fixture::new(1000, "ko");
    f.finish(1, "source", 0);
    let job = f.core.next_translation(0).unwrap().unwrap();
    let wrong = TranslationKey {
        request_id: job.key.request_id + 1,
        ..job.key
    };
    assert_eq!(
        f.core
            .complete_translation(wrong, Outcome::Text("bad".into()), 1),
        Err(CoreError::UnknownJob)
    );
    assert!(f.core.next_translation(1).unwrap().is_none());
    f.core
        .complete_translation(job.key, Outcome::Failed, 1)
        .unwrap();
    assert_eq!(f.records()[0].translation_state, TranslationState::Failed);
    assert_eq!(f.records()[0].source, "source");
}

#[test]
fn failed_empty_oversized_and_cancelled_results_reach_terminal_states() {
    for outcome in [
        Outcome::Failed,
        Outcome::Text(String::new()),
        Outcome::Text("x".repeat(MAX_TEXT_BYTES + 1)),
        Outcome::Cancelled,
    ] {
        let mut f = Fixture::new(1000, "ko");
        f.submit(1, AsrKind::Final, 512, 0);
        let job = f.core.next_asr().unwrap();
        f.core.complete_asr(job.key(), outcome, 0).unwrap();
        assert!(matches!(
            f.records()[0].source_state,
            SourceState::Failed | SourceState::Skipped
        ));
        assert!(f.core.next_translation(0).unwrap().is_none());
    }
    for outcome in [
        Outcome::Failed,
        Outcome::Text("\0".into()),
        Outcome::Text("x".repeat(MAX_TEXT_BYTES + 1)),
        Outcome::Cancelled,
    ] {
        let mut f = Fixture::new(1000, "ko");
        f.finish(1, "preserved", 0);
        let job = f.core.next_translation(0).unwrap().unwrap();
        f.core.complete_translation(job.key, outcome, 0).unwrap();
        assert!(matches!(
            f.records()[0].translation_state,
            TranslationState::Failed | TranslationState::Skipped
        ));
        assert_eq!(f.records()[0].source, "preserved");
    }
}

#[test]
fn paginated_history_rejects_mixed_versions_after_translation_update() {
    let mut f = Fixture::new(1000, "ko");
    f.finish(1, "한국어\n🙂", 0);
    f.finish(2, "日本語", 0);
    let version = f.core.version();
    let first = f.core.history_page(version, 0, 1).unwrap();
    assert_eq!(first.next_offset, Some(1));
    let tr = f.core.next_translation(0).unwrap().unwrap();
    f.core
        .complete_translation(tr.key, Outcome::Text("English\n🙂".into()), 1)
        .unwrap();
    assert_eq!(
        f.core.history_page(version, 1, 1),
        Err(CoreError::StaleSnapshot)
    );
    assert_eq!(first.records[0].source, "한국어\n🙂");
    let refreshed = f.core.history_page(f.core.version(), 0, 100).unwrap();
    assert_eq!(refreshed.records[0].translation, "English\n🙂");
    assert!(refreshed.version > version);
}

#[test]
fn history_is_bounded_and_evicted_segment_ids_cannot_be_reused() {
    let mut f = Fixture::new(3, "en");
    for n in 1..=12 {
        f.finish(n, "bounded", 0);
        assert!(f.records().len() <= 3);
    }
    assert_eq!(
        f.records()
            .iter()
            .map(|r| r.key.segment_id)
            .collect::<Vec<_>>(),
        vec![10, 11, 12]
    );
    assert_eq!(
        f.core.submit_asr(
            SegmentIdentity {
                audio: ID,
                segment_id: 1
            },
            SampleRange { start: 0, end: 512 },
            AsrKind::Final,
            &f.ring,
            &mut f.pool,
            0
        ),
        Err(CoreError::StaleIdentity)
    );
    let mut f = Fixture::new(1, "ko");
    f.submit(1, AsrKind::Final, 512, 0);
    assert_eq!(
        f.core.submit_asr(
            SegmentIdentity {
                audio: ID,
                segment_id: 2
            },
            SampleRange { start: 0, end: 512 },
            AsrKind::Final,
            &f.ring,
            &mut f.pool,
            0
        ),
        Err(CoreError::HistoryFull)
    );
    assert_eq!(f.core.queue_lengths().0, 1);
}

#[test]
fn stale_epoch_bad_range_and_backwards_clock_do_not_change_history() {
    let mut f = Fixture::new(1000, "en");
    f.submit(1, AsrKind::Partial, 512, 10);
    let version = f.core.version();
    for (id, range, now, expected) in [
        (
            SegmentIdentity {
                audio: AudioIdentity { epoch: 0, ..ID },
                segment_id: 2,
            },
            SampleRange { start: 0, end: 512 },
            10,
            CoreError::StaleIdentity,
        ),
        (
            SegmentIdentity {
                audio: ID,
                segment_id: 2,
            },
            SampleRange {
                start: 0,
                end: 128001,
            },
            10,
            CoreError::InvalidRange,
        ),
        (
            SegmentIdentity {
                audio: ID,
                segment_id: 2,
            },
            SampleRange { start: 0, end: 512 },
            9,
            CoreError::ClockRegression,
        ),
    ] {
        assert_eq!(
            f.core
                .submit_asr(id, range, AsrKind::Final, &f.ring, &mut f.pool, now),
            Err(expected)
        );
    }
    assert_eq!(f.core.version(), version);
    assert_eq!(f.core.restart(ID, false, 10), Err(CoreError::StaleIdentity));
    assert_eq!(
        f.core.restart(AudioIdentity { epoch: 2, ..ID }, true, 10),
        Err(CoreError::InvalidConfig)
    );
}

#[test]
fn vad_discard_cancels_inflight_without_flushing_a_final() {
    let mut f = Fixture::new(1000, "ko");
    f.submit(1, AsrKind::Partial, 512, 0);
    let job = f.core.next_asr().unwrap();
    f.core
        .discard_segment(
            SegmentIdentity {
                audio: ID,
                segment_id: 1,
            },
            1,
        )
        .unwrap();
    assert_eq!(f.core.cancellation().asr, Some(job.key()));
    assert_eq!(
        f.core
            .complete_asr(job.key(), Outcome::Text("late".into()), 2)
            .unwrap(),
        Apply::Ignored
    );
    assert_eq!(f.records()[0].source_state, SourceState::Discarded);
    assert!(f.core.next_asr().is_none());
}

#[test]
fn generated_pcm_flows_through_normalizer_vad_asr_translation_and_history() {
    let mut normalizer = StreamNormalizer::new(AudioFormat::new(16000, 1, None).unwrap(), ID, 0);
    let mut ring = RollingAudio::new(MAX_ROLLING_SAMPLES, ID, 0).unwrap();
    let mut pool = SnapshotPool::new(4, MAX_SNAPSHOT_SAMPLES).unwrap();
    let mut vad = VadSegmenter::new(ID, 0, VadSettings::default()).unwrap();
    let mut core = Pipeline::new(ID, MAX_HISTORY, "en", "ko").unwrap();
    let mut counts = (0, 0, 0);
    for i in 0..85 {
        let value = if (20..70).contains(&i) { 0.25 } else { 0.0 };
        let batch = normalizer.push(&[value; 512]).unwrap();
        for frame in batch.frames {
            let now = frame.range.end * 1_000_000_000 / 16000;
            ring.append(ID, frame.range.start, &frame.samples).unwrap();
            let p = VadSegmenter::requires_probability(&frame.samples).then_some(0.9);
            for event in vad.push(&frame, p, now).unwrap() {
                let request = match event {
                    SpeechEvent::Partial(s) => Some((s, AsrKind::Partial)),
                    SpeechEvent::Final { segment, .. } => Some((segment, AsrKind::Final)),
                    _ => None,
                };
                if let Some((s, kind)) = request {
                    assert!(
                        core.submit_asr(s.id, s.pcm_range, kind, &ring, &mut pool, now)
                            .unwrap()
                            .queued
                    );
                }
            }
            while let Some(job) = core.next_asr() {
                if job.kind == AsrKind::Partial {
                    counts.0 += 1;
                } else {
                    counts.1 += 1;
                }
                assert_eq!(
                    job.pcm.samples().len() as u64,
                    job.pcm.range().end - job.pcm.range().start
                );
                core.complete_asr(job.key(), Outcome::Text("No, no!".into()), now)
                    .unwrap();
            }
            if let Some(job) = core.next_translation(now).unwrap() {
                counts.2 += 1;
                core.complete_translation(job.key, Outcome::Text("안 돼, 안 돼!".into()), now)
                    .unwrap();
            }
        }
    }
    assert_eq!(counts, (2, 1, 1));
    let page = core.history_page(core.version(), 0, 100).unwrap();
    assert_eq!(page.records.len(), 1);
    assert_eq!(page.records[0].source_state, SourceState::Final);
    assert_eq!(page.records[0].translation_state, TranslationState::Done);
    assert_eq!(
        page.records[0].range,
        SampleRange {
            start: 11 * 512,
            end: 76 * 512
        }
    );
    assert_eq!(page.records[0].source, "No, no!");
    assert_eq!(page.records[0].translation, "안 돼, 안 돼!");
}

#[test]
fn epoch_restart_keeps_native_reservation_until_old_asr_returns() {
    let mut f = Fixture::new(1000, "en");
    f.submit(1, AsrKind::Final, 512, 0);
    let old = f.core.next_asr().unwrap();
    let new = AudioIdentity { epoch: 2, ..ID };
    f.core.restart(new, false, 1).unwrap();
    f.ring.reset(new, MAX_ROLLING_SAMPLES as u64).unwrap();
    f.ring
        .append(new, MAX_ROLLING_SAMPLES as u64, &[0.75; 512])
        .unwrap();
    let admitted = f
        .core
        .submit_asr(
            SegmentIdentity {
                audio: new,
                segment_id: 2,
            },
            SampleRange {
                start: MAX_ROLLING_SAMPLES as u64,
                end: MAX_ROLLING_SAMPLES as u64 + 512,
            },
            AsrKind::Final,
            &f.ring,
            &mut f.pool,
            1,
        )
        .unwrap();
    assert!(admitted.queued);
    assert!(f.core.next_asr().is_none());
    assert_eq!(
        f.core
            .complete_asr(old.key(), Outcome::Text("old epoch".into()), 2)
            .unwrap(),
        Apply::Ignored
    );
    drop(old);
    let fresh = f.core.next_asr().unwrap();
    assert_eq!(fresh.key(), admitted.key);
    assert!(fresh.pcm.samples().iter().all(|x| *x == 0.75));
    f.core
        .complete_asr(fresh.key(), Outcome::Text("fresh epoch".into()), 3)
        .unwrap();
    assert_eq!(f.records()[1].source, "fresh epoch");
    assert_eq!(f.records()[0].source_state, SourceState::Discarded);
}

#[test]
fn default_history_capacity_is_enforced_after_twelve_hundred_finals() {
    let mut f = Fixture::new(MAX_HISTORY, "en");
    for n in 1..=1200 {
        f.finish(n, "same", 0);
    }
    let mut offset = 0;
    let mut rows = Vec::new();
    loop {
        let page = f.core.history_page(f.core.version(), offset, 100).unwrap();
        rows.extend(page.records);
        if let Some(next) = page.next_offset {
            offset = next;
        } else {
            break;
        }
    }
    assert_eq!(rows.len(), 1000);
    assert_eq!(rows.first().unwrap().key.segment_id, 201);
    assert_eq!(rows.last().unwrap().key.segment_id, 1200);
    assert_eq!(f.core.queue_lengths(), (0, 0, 0));
}

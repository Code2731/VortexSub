//! VAD segment IDs restart per epoch; product segment IDs span the session.
use super::Runtime;
use crate::transport::Outbox;
use echosub_audio_core::{SegmentIdentity, SegmentInfo, SpeechEvent};
use echosub_pipeline_core::AsrKind;
use serde_json::json;
use std::io;

#[derive(Default)]
pub(super) struct PartialSchedule {
    pending: Option<(SegmentInfo, u64)>,
    requested: u64,
    deferred: u64,
    replaced: u64,
    dropped: u64,
    pub(super) applied: u64,
    pub(super) ignored: u64,
    pub(super) decode_s: f64,
    wait_s: f64,
    pub(super) adaptive: crate::adaptive_partial::AdaptivePartial,
    adaptive_deferred: u64,
}
impl PartialSchedule {
    pub(super) fn clear(&mut self) {
        self.pending = None;
        self.adaptive.reset();
    }
    pub(super) fn value(&self) -> serde_json::Value {
        json!({"pending":self.pending.is_some(),"requested":self.requested,
            "deferred":self.deferred,"replaced":self.replaced,"dropped":self.dropped,
            "asr_applied":self.applied,"asr_ignored":self.ignored,
            "asr_decode_total_s":self.decode_s,"last_deferred_wait_s":self.wait_s,
            "adaptive_deferred":self.adaptive_deferred,"adaptive_growth_s":self.adaptive.growth_s(),
            "adaptive_policy":self.adaptive.decision})
    }
}

impl Runtime {
    pub(super) fn live_event(&mut self, event: SpeechEvent, q: &Outbox) -> io::Result<()> {
        match event {
            SpeechEvent::Partial(segment) if self.partial_enabled => {
                if segment.id.audio != self.epoch {
                    return Ok(());
                }
                self.partial_schedule.requested += 1;
                let adaptive_wait = self.fast_partials
                    && !self
                        .partial_schedule
                        .adaptive
                        .due(segment.id, segment.pcm_range.end);
                if adaptive_wait {
                    self.partial_schedule.adaptive_deferred += 1;
                }
                if self.partial_busy() || adaptive_wait {
                    self.partial_schedule.deferred += 1;
                    if self
                        .partial_schedule
                        .pending
                        .replace((segment, self.now()))
                        .is_some()
                    {
                        self.partial_schedule.replaced += 1;
                    }
                } else {
                    self.partial_schedule.pending = None;
                    self.live_asr_segment(segment, AsrKind::Partial, "Partial", q)?;
                }
            }
            SpeechEvent::Final { segment, reason } => {
                if segment.id.audio != self.epoch {
                    return Ok(());
                }
                self.partial_schedule.clear();
                self.window_state.clear();
                self.live_asr_segment(segment, AsrKind::Final, &format!("{reason:?}"), q)?;
            }
            SpeechEvent::Discarded { segment, reason } => {
                if segment.id.audio != self.epoch {
                    return Ok(());
                }
                self.partial_schedule.clear();
                self.window_state.clear();
                if let Some((vad, id)) = self.live_segment.filter(|(vad, _)| *vad == segment.id) {
                    self.core
                        .discard_segment(id, self.now())
                        .map_err(|_| io::Error::other("Live partial discard rejected"))?;
                    self.cancel_native_if_requested();
                    self.live_segment = None;
                    self.prune_languages();
                    self.changed_record(id.segment_id, "segment.discarded", q)
                        .map_err(|_| io::Error::other("Live discard event unavailable"))?;
                    debug_assert_eq!(vad, segment.id);
                }
                q.publish("capture.segment_discarded",json!({"epoch":self.epoch.epoch,"vad_segment_id":segment.id.segment_id,"reason":format!("{reason:?}")}),None)?;
            }
            _ => {}
        }
        Ok(())
    }
    fn partial_busy(&self) -> bool {
        // Do not change the active ASR key, or cancel a preview before its HTTP
        // result can return. Final requests still bypass this gate.
        self.flight.is_some() || self.core.queue_lengths().0 > 0 || self.core.preview_pending()
    }
    pub(super) fn flush_partial(&mut self, q: &Outbox) -> io::Result<()> {
        if self.partial_busy() {
            return Ok(());
        }
        let Some((segment, requested)) = self.partial_schedule.pending.take() else {
            return Ok(());
        };
        let retained = self.ring.retained_range();
        if segment.id.audio != self.epoch
            || !self.partial_enabled
            || segment.pcm_range.start < retained.start
            || segment.pcm_range.end > retained.end
        {
            self.partial_schedule.dropped += 1;
            return Ok(());
        }
        if self.fast_partials
            && !self
                .partial_schedule
                .adaptive
                .due(segment.id, segment.pcm_range.end)
        {
            self.partial_schedule.pending = Some((segment, requested));
            return Ok(());
        }
        self.partial_schedule.wait_s = self.now().saturating_sub(requested) as f64 / 1e9;
        self.live_asr_segment(segment, AsrKind::Partial, "DeferredLatest", q)
    }
    fn live_asr_segment(
        &mut self,
        segment: SegmentInfo,
        kind: AsrKind,
        reason: &str,
        q: &Outbox,
    ) -> io::Result<()> {
        if segment.id.audio != self.epoch {
            return Ok(());
        }
        let id = match self.live_segment {
            Some((vad, id)) if vad == segment.id => id,
            Some(_) => return Err(io::Error::other("Overlapping live VAD segments")),
            None => {
                let id = SegmentIdentity {
                    audio: self.epoch,
                    segment_id: self.next_segment,
                };
                self.next_segment = self
                    .next_segment
                    .checked_add(1)
                    .ok_or_else(|| io::Error::other("Segment identity exhausted"))?;
                id
            }
        };
        let now = self.now();
        let admitted = self
            .core
            .submit_asr(id, segment.pcm_range, kind, &self.ring, &mut self.pool, now)
            .map_err(|_| io::Error::other("Live ASR range rejected"))?;
        self.live_segment = if kind == AsrKind::Partial {
            Some((segment.id, id))
        } else {
            None
        };
        self.cancel_native_if_requested();
        // Replaced partials must not accumulate stale language entries.
        self.prune_languages();
        if admitted.queued {
            if self.fast_partials && kind == AsrKind::Partial {
                self.partial_schedule
                    .adaptive
                    .admitted(segment.id, segment.pcm_range.end);
            }
            self.languages
                .push((admitted.key, self.live_language.clone()));
            if let Some((_, previous)) = self
                .last_live_final
                .filter(|(vad, _)| Some(*vad) == segment.continued_from)
            {
                self.continuations.push((admitted.key, previous));
            }
        } else if kind == AsrKind::Final {
            self.changed_record(id.segment_id, "segment.skipped", q)
                .map_err(|_| io::Error::other("Live skip event unavailable"))?;
        }
        if kind == AsrKind::Final {
            self.last_live_final = Some((segment.id, id));
        }
        let event = if kind == AsrKind::Final {
            "capture.segmented"
        } else {
            "capture.partial_requested"
        };
        let coalesce = (kind == AsrKind::Partial).then(|| {
            format!(
                "partial-request/{}/{}/{}",
                id.audio.session_id, id.audio.epoch, id.segment_id
            )
        });
        q.publish(event,json!({"worker_at_s":now as f64/1e9,"session_id":id.audio.session_id,"adaptive_growth_s":if self.fast_partials {Some(self.partial_schedule.adaptive.growth_s())} else {None},"adaptive_policy":if self.fast_partials {self.partial_schedule.adaptive.decision} else {"Fixed"},"partial_deferred_wait_s":if reason == "DeferredLatest" {self.partial_schedule.wait_s} else {0.},"voice_start_s":segment.voice_range.start_s(),"voice_end_s":segment.voice_range.end_s(),"epoch":id.audio.epoch,"segment_id":id.segment_id,"source_revision":admitted.key.source_revision,"vad_segment_id":segment.id.segment_id,"continued_from":segment.continued_from.map(|s|s.segment_id),"queued":admitted.queued,"reason":reason,"audio_start_s":segment.pcm_range.start_s(),"audio_end_s":segment.pcm_range.end_s()}),coalesce)?;
        Ok(())
    }
    fn cancel_native_if_requested(&self) {
        if let Some(cancel) = self.core.cancellation().asr {
            if let Some((key, token)) = &self.flight {
                if *key == cancel {
                    token.request();
                }
            }
        }
    }
    fn prune_languages(&mut self) {
        self.continuations
            .retain(|(key, _)| self.core.pending_asr_keys().any(|queued| queued == *key));
        self.languages
            .retain(|(key, _)| self.core.pending_asr_keys().any(|queued| queued == *key));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use echosub_asr_whisper::Cancellation;
    use echosub_audio_core::{FinalReason, SampleRange};
    use echosub_pipeline_core::{Apply, Outcome, SourceState};
    fn segment(r: &Runtime, vad_id: u64, end: u64) -> SegmentInfo {
        SegmentInfo {
            id: SegmentIdentity {
                audio: r.epoch,
                segment_id: vad_id,
            },
            pcm_range: SampleRange { start: 0, end },
            voice_range: SampleRange { start: 0, end },
            voiced_samples: end,
            new_voiced_samples: end,
            continued_from: None,
        }
    }
    #[test]
    fn slow_partial_applies_before_latest_deferred_request_changes_revision() {
        let mut r = Runtime::new(false, None, false);
        r.core = echosub_pipeline_core::Pipeline::new_asr_only(r.epoch, 1000).unwrap();
        r.partial_enabled = true;
        r.ring.append(r.epoch, 0, &vec![0.2; 64000]).unwrap();
        let q = Outbox::default();
        r.live_event(SpeechEvent::Partial(segment(&r, 1, 16000)), &q)
            .unwrap();
        let first = r.core.next_asr().unwrap();
        let token = Cancellation::default();
        r.flight = Some((first.key(), token.clone()));
        for end in [24000, 32000, 40000] {
            r.live_event(SpeechEvent::Partial(segment(&r, 1, end)), &q)
                .unwrap();
            r.flush_partial(&q).unwrap();
        }
        assert!(!token.snapshot().requested);
        assert_eq!(r.partial_schedule.pending.unwrap().0.pcm_range.end, 40000);
        assert_eq!(r.partial_schedule.deferred, 3);
        assert_eq!(r.partial_schedule.replaced, 2);
        assert_eq!(
            r.core
                .complete_asr(
                    first.key(),
                    Outcome::Text("read this result".into()),
                    r.now()
                )
                .unwrap(),
            Apply::Applied
        );
        r.flight = None;
        drop(first);
        r.flush_partial(&q).unwrap();
        let latest = r.core.next_asr().unwrap();
        assert_eq!(latest.key().source_revision, 2);
        assert_eq!(latest.pcm.range().end, 40000);
        assert!(r.partial_schedule.pending.is_none());
    }
    #[test]
    fn deferred_partial_waits_for_preview_http_return_even_after_cancellation() {
        let mut r = Runtime::new(false, None, false);
        r.core.set_partial_translation_enabled(true);
        r.partial_enabled = true;
        r.ring.append(r.epoch, 0, &vec![0.2; 64000]).unwrap();
        let q = Outbox::default();
        for end in [16000, 24000] {
            r.live_event(SpeechEvent::Partial(segment(&r, 1, end)), &q)
                .unwrap();
            let job = r.core.next_asr().unwrap();
            r.core
                .complete_asr(
                    job.key(),
                    Outcome::Text("please cross the bridge now".into()),
                    r.now(),
                )
                .unwrap();
        }
        assert!(r.core.preview_pending());
        let preview = r.core.next_translation(r.now()).unwrap().unwrap();
        r.live_event(SpeechEvent::Partial(segment(&r, 1, 32000)), &q)
            .unwrap();
        r.flush_partial(&q).unwrap();
        assert!(r.core.next_asr().is_none());
        r.core.poll(preview.deadline_ns).unwrap();
        assert!(r.core.preview_pending());
        assert_eq!(
            r.core
                .complete_translation(
                    preview.key,
                    Outcome::Text("late preview".into()),
                    preview.deadline_ns
                )
                .unwrap(),
            Apply::Ignored
        );
        // Use no further core mutation with the real clock after this synthetic deadline.
        assert!(!r.core.preview_pending());
    }
    #[test]
    fn adaptive_pending_waits_for_new_audio_and_final_bypasses_feedback() {
        let mut r = Runtime::new(false, None, false);
        r.core = echosub_pipeline_core::Pipeline::new_asr_only(r.epoch, 1000).unwrap();
        r.partial_enabled = true;
        r.fast_partials = true;
        r.ring.append(r.epoch, 0, &vec![0.2; 64000]).unwrap();
        let q = Outbox::default();
        r.live_event(SpeechEvent::Partial(segment(&r, 1, 12800)), &q)
            .unwrap();
        let first = r.core.next_asr().unwrap();
        r.core
            .complete_asr(
                first.key(),
                Outcome::Text("Take the left path.".into()),
                r.now(),
            )
            .unwrap();
        drop(first);
        let vad = segment(&r, 1, 12800).id;
        r.partial_schedule
            .adaptive
            .feedback(vad, Some("Take the left path."), "", 0.05);
        r.live_event(SpeechEvent::Partial(segment(&r, 1, 16896)), &q)
            .unwrap();
        let confirm = r.core.next_asr().unwrap();
        assert_eq!(confirm.pcm.range().end, 16896);
        r.core
            .complete_asr(
                confirm.key(),
                Outcome::Text("Take the left path.".into()),
                r.now(),
            )
            .unwrap();
        drop(confirm);
        r.partial_schedule.adaptive.feedback(
            vad,
            Some("Take the left path."),
            "Take the left path.",
            0.05,
        );
        r.live_event(SpeechEvent::Partial(segment(&r, 1, 20992)), &q)
            .unwrap();
        r.flush_partial(&q).unwrap();
        assert!(r.core.next_asr().is_none());
        assert!(r.partial_schedule.pending.is_some());
        r.live_event(
            SpeechEvent::Final {
                segment: segment(&r, 1, 20992),
                reason: FinalReason::Silence,
            },
            &q,
        )
        .unwrap();
        assert!(r.partial_schedule.pending.is_none());
        assert_eq!(r.core.next_asr().unwrap().kind, AsrKind::Final);
        assert_eq!(r.partial_schedule.adaptive.decision, "Initial");
    }
    #[test]
    fn overwritten_deferred_audio_is_dropped_without_admitting_a_job() {
        let mut r = Runtime::new(false, None, false);
        r.partial_enabled = true;
        r.ring.append(r.epoch, 0, &vec![0.2; 16000]).unwrap();
        r.partial_schedule.pending = Some((segment(&r, 1, 16000), r.now()));
        r.ring.append(r.epoch, 16000, &vec![0.2; 320000]).unwrap();
        r.flush_partial(&Outbox::default()).unwrap();
        assert_eq!(r.partial_schedule.dropped, 1);
        assert!(r.core.next_asr().is_none());
        assert!(r.partial_schedule.pending.is_none());
    }
    /// Explicit local diagnostic: real native owner, paced PCM, known file end.
    /// VAD, HTTP, overlay and capture are intentionally outside this measurement.
    #[cfg(feature = "native-asr")]
    #[test]
    #[ignore = "requires consented model/WAV paths via ECHOSUB_SCHEDULE_*"]
    fn native_paced_partial_probe() {
        use crate::native_owner::{load_wav, ModelConfig};
        use sha2::{Digest, Sha256};
        use std::time::{Duration, Instant};
        let model = std::env::var("ECHOSUB_SCHEDULE_MODEL").unwrap();
        let wav = std::env::var("ECHOSUB_SCHEDULE_WAV").unwrap();
        let output = std::env::var("ECHOSUB_SCHEDULE_REPORT").unwrap();
        let backend = std::env::var("ECHOSUB_SCHEDULE_BACKEND").unwrap_or("cpu".into());
        assert!(matches!(backend.as_str(), "cpu" | "cuda"));
        let gpu = backend == "cuda";
        assert!(!gpu || cfg!(feature = "cuda"), "CUDA build required");
        let rounds: usize = std::env::var("ECHOSUB_SCHEDULE_ROUNDS")
            .unwrap_or("1".into())
            .parse()
            .unwrap();
        assert!((1..=10).contains(&rounds));
        let first_request_s: f64 = std::env::var("ECHOSUB_SCHEDULE_FIRST")
            .unwrap_or("0.8".into())
            .parse()
            .unwrap();
        assert!((0.8..=2.).contains(&first_request_s));
        let current_only = std::env::var("ECHOSUB_SCHEDULE_CURRENT").as_deref() == Ok("1");
        let model_hash = format!("{:x}", Sha256::digest(std::fs::read(&model).unwrap()));
        let wav_hash = format!("{:x}", Sha256::digest(std::fs::read(&wav).unwrap()));
        let pcm = load_wav(&wav, &wav_hash).unwrap();
        assert!(
            pcm.len() >= 16000 && pcm.len() <= 128000,
            "use a 1–8 second fixture"
        );
        let mut reports = Vec::new();
        for round in 1..=rounds {
            for interval_s in [1.0, 0.25] {
                for legacy in [true, false]
                    .into_iter()
                    .filter(|legacy| !current_only || !legacy)
                {
                    let mut r = Runtime::new(
                        false,
                        Some(ModelConfig {
                            path: model.clone(),
                            hash: model_hash.clone(),
                            gpu,
                            threads: 8,
                            vad: None,
                            decode_window: false,
                            pad_short_partials: false,
                        }),
                        false,
                    );
                    r.partial_enabled = true;
                    let q = Outbox::default();
                    let load_start = Instant::now();
                    while r.model_state != "Ready" {
                        r.poll_native(&q).unwrap();
                        assert!(
                            load_start.elapsed().as_secs_f64() < 30. && r.model_state != "Failed"
                        );
                        std::thread::sleep(Duration::from_millis(5));
                    }
                    let load_s = load_start.elapsed().as_secs_f64();
                    let start = Instant::now();
                    let mut cursor = 0;
                    let mut next_partial_s = first_request_s;
                    let mut final_sent = false;
                    let mut first_partial_s = None;
                    let mut admitted = 0;
                    let mut updates = Vec::new();
                    let mut last_revision = None;
                    let final_s = loop {
                        let elapsed = start.elapsed().as_secs_f64();
                        assert!(
                            elapsed < pcm.len() as f64 / 16000. + 30.,
                            "native replay did not drain"
                        );
                        let available = ((elapsed * 16000.) as usize).min(pcm.len());
                        if available > cursor {
                            r.ring
                                .append(r.epoch, cursor as u64, &pcm[cursor..available])
                                .unwrap();
                            cursor = available;
                        }
                        if cursor == pcm.len() && !final_sent {
                            r.live_event(
                                SpeechEvent::Final {
                                    segment: segment(&r, 1, cursor as u64),
                                    reason: FinalReason::Silence,
                                },
                                &q,
                            )
                            .unwrap();
                            final_sent = true;
                        } else if !final_sent && elapsed >= next_partial_s {
                            admitted += 1;
                            if legacy {
                                r.live_asr_segment(
                                    segment(&r, 1, cursor as u64),
                                    AsrKind::Partial,
                                    "LegacyProbe",
                                    &q,
                                )
                                .unwrap();
                            } else {
                                r.live_event(
                                    SpeechEvent::Partial(segment(&r, 1, cursor as u64)),
                                    &q,
                                )
                                .unwrap();
                            }
                            next_partial_s += interval_s;
                        }
                        r.poll_native(&q).unwrap();
                        if let Some(record) = r.core.record(SegmentIdentity {
                            audio: r.epoch,
                            segment_id: 1,
                        }) {
                            if record.applied_source_revision != last_revision {
                                last_revision = record.applied_source_revision;
                                if last_revision.is_some() {
                                    updates.push(json!({"at_s":elapsed,"revision":last_revision,"state":format!("{:?}",record.source_state),"source":record.source}));
                                    if record.source_state == SourceState::Partial
                                        && first_partial_s.is_none()
                                    {
                                        first_partial_s = Some(elapsed);
                                    }
                                }
                            }
                            if record.source_state == SourceState::Final {
                                break elapsed;
                            }
                            assert!(
                                !matches!(
                                    record.source_state,
                                    SourceState::Failed | SourceState::Skipped
                                ),
                                "native source failed"
                            );
                        }
                        std::thread::sleep(Duration::from_millis(5));
                    };
                    let report = json!({"round":round,"load_s":load_s,"legacy_admission":legacy,"interval_s":interval_s,"partial_events":admitted,"audio_s":pcm.len() as f64/16000.,"first_partial_s":first_partial_s,"final_s":final_s,"scheduler":r.partial_schedule.value(),"updates":updates});
                    println!("{}", report);
                    reports.push(report);
                    r.finish();
                }
            }
        }
        std::fs::write(output, serde_json::to_vec_pretty(&json!({"model":model,"model_sha256":model_hash,"wav":wav,"wav_sha256":wav_hash,"backend":backend,"threads":8,"first_request_s":first_request_s,"rounds":rounds,"current_only":current_only,"note":"Fresh owner each run, no warmup; load excluded from replay timing. Known file end; ASR-only, no VAD/HTTP/capture/overlay. 0.25 s is stress, not the production default.","runs":reports})).unwrap()).unwrap();
    }
    #[test]
    fn continuation_context_is_product_identity_and_pending_metadata_stays_bounded() {
        let mut r = Runtime::new(false, None, false);
        r.ring.append(r.epoch, 0, &vec![0.2; 128000]).unwrap();
        let q = Outbox::default();
        r.live_event(
            SpeechEvent::Final {
                segment: segment(&r, 1, 128000),
                reason: FinalReason::ChunkLimit,
            },
            &q,
        )
        .unwrap();
        r.ring.append(r.epoch, 128000, &vec![0.2; 38400]).unwrap();
        r.partial_enabled = true;
        for end in [134400, 150400, 166400] {
            let mut next = segment(&r, 2, end);
            next.pcm_range.start = 118400;
            next.continued_from = Some(segment(&r, 1, 128000).id);
            r.live_event(SpeechEvent::Partial(next), &q).unwrap();
            assert_eq!(r.continuations.len(), 0);
            assert!(r.partial_schedule.pending.is_some());
            assert!(r.languages.len() <= 3);
        }
        r.ring.append(r.epoch, 166400, &vec![0.2; 80000]).unwrap();
        let mut final_segment = segment(&r, 2, 246400);
        final_segment.pcm_range.start = 118400;
        final_segment.continued_from = Some(segment(&r, 1, 128000).id);
        r.live_event(
            SpeechEvent::Final {
                segment: final_segment,
                reason: FinalReason::Silence,
            },
            &q,
        )
        .unwrap();
        assert_eq!(r.continuations.len(), 1);
        assert_eq!(r.last_live_final.unwrap().1.segment_id, 2);
    }
    #[test]
    fn final_cancels_partial_but_retains_native_reservation_and_prunes_replacements() {
        let mut r = Runtime::new(false, None, false);
        r.core = echosub_pipeline_core::Pipeline::new_asr_only(r.epoch, 1000).unwrap();
        r.partial_enabled = true;
        r.ring.append(r.epoch, 0, &vec![0.2; 64000]).unwrap();
        let q = Outbox::default();
        r.live_event(SpeechEvent::Partial(segment(&r, 1, 16000)), &q)
            .unwrap();
        let job = r.core.next_asr().unwrap();
        let token = Cancellation::default();
        r.flight = Some((job.key(), token.clone()));
        for end in [24000, 32000, 40000] {
            r.live_event(SpeechEvent::Partial(segment(&r, 1, end)), &q)
                .unwrap();
            assert_eq!(r.languages.len(), 1);
            assert!(r.core.next_asr().is_none());
        }
        r.live_event(
            SpeechEvent::Final {
                segment: segment(&r, 1, 48000),
                reason: FinalReason::Silence,
            },
            &q,
        )
        .unwrap();
        assert!(token.snapshot().requested);
        assert!(r.core.next_asr().is_none());
        assert_eq!(
            r.core
                .complete_asr(job.key(), Outcome::Text("late partial".into()), r.now())
                .unwrap(),
            Apply::Ignored
        );
        r.flight = None;
        drop(job);
        let final_job = r.core.next_asr().unwrap();
        assert_eq!(final_job.kind, AsrKind::Final);
        assert_eq!(final_job.key().segment_id, 1);
        assert_eq!(final_job.key().source_revision, 2);
        r.core
            .complete_asr(final_job.key(), Outcome::Text("confirmed".into()), r.now())
            .unwrap();
        let record = r
            .core
            .record(SegmentIdentity {
                audio: r.epoch,
                segment_id: 1,
            })
            .unwrap();
        assert_eq!(record.source_state, SourceState::Final);
        assert_eq!(record.source, "confirmed");
        let mut stale = segment(&r, 1, 16000);
        stale.id.audio.epoch += 1;
        r.live_event(SpeechEvent::Partial(stale), &q).unwrap();
        assert!(r.live_segment.is_none());
        r.live_event(SpeechEvent::Partial(segment(&r, 2, 16000)), &q)
            .unwrap();
        assert_eq!(r.live_segment.unwrap().1.segment_id, 2);
    }
}

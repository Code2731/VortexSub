//! VAD segment IDs restart per epoch; product segment IDs span the session.
use super::Runtime;
use crate::transport::Outbox;
use echosub_audio_core::{SegmentIdentity, SegmentInfo, SpeechEvent};
use echosub_pipeline_core::AsrKind;
use serde_json::json;
use std::io;

impl Runtime {
    pub(super) fn live_event(&mut self, event: SpeechEvent, q: &Outbox) -> io::Result<()> {
        match event {
            SpeechEvent::Partial(segment) if self.partial_enabled => {
                self.live_asr_segment(segment, AsrKind::Partial, "Partial", q)?;
            }
            SpeechEvent::Final { segment, reason } => {
                self.live_asr_segment(segment, AsrKind::Final, &format!("{reason:?}"), q)?;
            }
            SpeechEvent::Discarded { segment, reason } => {
                if segment.id.audio != self.epoch {
                    return Ok(());
                }
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
            self.languages
                .push((admitted.key, self.live_language.clone()));
        } else if kind == AsrKind::Final {
            self.changed_record(id.segment_id, "segment.skipped", q)
                .map_err(|_| io::Error::other("Live skip event unavailable"))?;
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
        q.publish(event,json!({"epoch":id.audio.epoch,"segment_id":id.segment_id,"source_revision":admitted.key.source_revision,"vad_segment_id":segment.id.segment_id,"continued_from":segment.continued_from.map(|s|s.segment_id),"queued":admitted.queued,"reason":reason,"audio_start_s":segment.pcm_range.start_s(),"audio_end_s":segment.pcm_range.end_s()}),coalesce)?;
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
        assert_eq!(final_job.key().source_revision, 5);
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

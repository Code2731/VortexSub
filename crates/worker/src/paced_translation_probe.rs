//! Explicit file diagnostic using the real native/HTTP owners and production scheduler.
use super::*;
use echosub_audio_core::{FinalReason, SegmentInfo, SpeechEvent};
use echosub_pipeline_core::{SourceState, TranslationState};
use sha2::{Digest, Sha256};
use std::time::Duration;

fn segment(r: &Runtime, end: u64) -> SegmentInfo {
    SegmentInfo {
        id: SegmentIdentity {
            audio: r.epoch,
            segment_id: 1,
        },
        pcm_range: SampleRange { start: 0, end },
        voice_range: SampleRange { start: 0, end },
        voiced_samples: end,
        new_voiced_samples: end,
        continued_from: None,
    }
}

#[test]
#[ignore = "existing model/WAV and owned local HTTP server required; file only"]
fn native_paced_translation_probe() {
    let model = std::env::var("ECHOSUB_SCHEDULE_MODEL").unwrap();
    let wav = std::env::var("ECHOSUB_SCHEDULE_WAV").unwrap();
    let output = std::env::var("ECHOSUB_SCHEDULE_REPORT").unwrap();
    let backend = std::env::var("ECHOSUB_SCHEDULE_BACKEND").unwrap();
    assert!(matches!(backend.as_str(), "cpu" | "cuda"));
    assert!(backend != "cuda" || cfg!(feature = "cuda"));
    let endpoint = std::env::var("ECHOSUB_SCHEDULE_ENDPOINT").unwrap();
    let translation_model = std::env::var("ECHOSUB_SCHEDULE_TRANSLATION_MODEL").unwrap();
    let rounds: usize = std::env::var("ECHOSUB_SCHEDULE_ROUNDS")
        .unwrap()
        .parse()
        .unwrap();
    assert!((1..=10).contains(&rounds));
    let first_s: f64 = std::env::var("ECHOSUB_SCHEDULE_FIRST")
        .unwrap()
        .parse()
        .unwrap();
    assert!((0.8..=2.).contains(&first_s));
    let hash = |path: &str| format!("{:x}", Sha256::digest(std::fs::read(path).unwrap()));
    let model_hash = hash(&model);
    let wav_hash = hash(&wav);
    let pcm = crate::native_owner::load_wav(&wav, &wav_hash).unwrap();
    assert!((16000..=128000).contains(&pcm.len()));
    let adaptive_compare = std::env::var("ECHOSUB_ADAPTIVE_COMPARE").as_deref() == Ok("1");
    let window_compare = std::env::var("ECHOSUB_WINDOW_COMPARE").as_deref() == Ok("1");
    let padding_compare = std::env::var("ECHOSUB_PADDING_COMPARE").as_deref() == Ok("1");
    let supported_compare = std::env::var("ECHOSUB_SUPPORTED_COMPARE").as_deref() == Ok("1");
    assert!(!(supported_compare && (padding_compare || window_compare || adaptive_compare)));
    assert!(!(padding_compare && (window_compare || adaptive_compare)));
    let mut reports = Vec::new();
    for round in 1..=rounds {
        // Alternate interval order within this backend; no claims of full order balancing.
        let intervals = if window_compare || padding_compare || supported_compare {
            [0.25, 0.25]
        } else if adaptive_compare {
            if round % 2 == 1 {
                [0.5, 0.25]
            } else {
                [0.25, 0.5]
            }
        } else if round % 2 == 1 {
            [1., 0.5]
        } else {
            [0.5, 1.]
        };
        for (condition, interval_s) in intervals.into_iter().enumerate() {
            let decode_window = window_compare && (condition == usize::from(round % 2 == 1));
            let pad_short_partials =
                supported_compare || padding_compare && (condition == usize::from(round % 2 == 1));
            let supported_preview = supported_compare && (condition == usize::from(round % 2 == 1));
            let mut r = Runtime::new(
                false,
                Some(ModelConfig {
                    path: model.clone(),
                    hash: model_hash.clone(),
                    gpu: backend == "cuda",
                    threads: 8,
                    vad: None,
                    decode_window,
                    pad_short_partials,
                }),
                false,
            );
            let q = Outbox::default();
            r.core.set_supported_preview_enabled(supported_preview);
            let setup = Instant::now();
            while r.model_state != "Ready" {
                r.poll_native(&q).unwrap();
                assert!(setup.elapsed().as_secs_f64() < 30. && r.model_state != "Failed");
                std::thread::sleep(Duration::from_millis(5));
            }
            r.enable_http_translation();
            r.translation_command(
                "configure_translation",
                &json!({"endpoint":endpoint,"model_id":translation_model}),
                &q,
            )
            .unwrap();
            while r.translator.value(false)["state"] != "Ready" {
                r.poll_translation(&q).unwrap();
                assert!(
                    setup.elapsed().as_secs_f64() < 40.
                        && r.translator.value(false)["state"] != "Failed"
                );
                std::thread::sleep(Duration::from_millis(5));
            }
            r.fast_partials = supported_compare
                || padding_compare
                || window_compare
                || adaptive_compare && interval_s == 0.25;
            r.partial_enabled = true;
            r.core.set_partial_translation_enabled(true);
            let setup_s = setup.elapsed().as_secs_f64();
            q.drain_probe_events();
            let start = Instant::now();
            let mut cursor = 0;
            let effective_interval_s = (interval_s * 16000_f64 / 512.).round() * 512. / 16000.;
            let mut next_s = first_s;
            let mut final_sent = false;
            let mut final_asr_s = None;
            let mut first_text_s = None;
            let mut first_stable_s = None;
            let mut first_translation_s = None;
            let mut events = Vec::new();
            let mut requests = Vec::new();
            let mut last_request = None;
            let final_translation_s = loop {
                let elapsed = start.elapsed().as_secs_f64();
                assert!(
                    elapsed < pcm.len() as f64 / 16000. + 30.,
                    "paced HTTP replay did not drain"
                );
                let available = ((elapsed * 16000.) as usize).min(pcm.len());
                let available = if available == pcm.len() {
                    available
                } else {
                    available / 512 * 512
                };
                if available > cursor {
                    r.ring
                        .append(r.epoch, cursor as u64, &pcm[cursor..available])
                        .unwrap();
                    cursor = available;
                }
                if cursor == pcm.len() && !final_sent {
                    r.live_event(
                        SpeechEvent::Final {
                            segment: segment(&r, cursor as u64),
                            reason: FinalReason::Silence,
                        },
                        &q,
                    )
                    .unwrap();
                    final_sent = true;
                } else if !final_sent && elapsed >= next_s {
                    r.live_event(SpeechEvent::Partial(segment(&r, cursor as u64)), &q)
                        .unwrap();
                    next_s += effective_interval_s;
                }
                r.poll(&q).unwrap();
                let observed = start.elapsed().as_secs_f64();
                if let Some(job) = &r.translation {
                    if last_request != Some(job.key.request_id) {
                        last_request = Some(job.key.request_id);
                        requests.push(json!({"at_s":observed,"request_id":job.key.request_id,
                            "source_revision":job.key.source.source_revision,"source":job.source,
                            "context":job.context}));
                    }
                }
                for event in q.drain_probe_events() {
                    events.push(json!({"at_s":observed,"message":event}));
                }
                if let Some(record) = r.core.record(SegmentIdentity {
                    audio: r.epoch,
                    segment_id: 1,
                }) {
                    if !record.source.is_empty() {
                        first_text_s.get_or_insert(observed);
                    }
                    if !record.stable_source.is_empty() {
                        first_stable_s.get_or_insert(observed);
                    }
                    if record.translation_state == TranslationState::Done
                        && !record.translation.is_empty()
                    {
                        first_translation_s.get_or_insert(observed);
                    }
                    if record.source_state == SourceState::Final {
                        final_asr_s.get_or_insert(observed);
                        if record.translation_state == TranslationState::Done {
                            break observed;
                        }
                        assert!(
                            !matches!(
                                record.translation_state,
                                TranslationState::Failed | TranslationState::Skipped
                            ),
                            "final HTTP failed"
                        );
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
            let record = r
                .core
                .record(SegmentIdentity {
                    audio: r.epoch,
                    segment_id: 1,
                })
                .unwrap();
            reports.push(json!({"round":round,"interval_s":interval_s,"effective_interval_s":effective_interval_s,"adaptive":r.fast_partials,"supported_preview":supported_preview,"pad_short_partials":pad_short_partials,"decode_window":decode_window,"window_attempts":r.window_state.attempts,"window_fallbacks":r.window_state.fallbacks,"setup_s":setup_s,
                "audio_s":pcm.len() as f64/16000.,"first_text_s":first_text_s,"first_stable_s":first_stable_s,
                "first_translation_s":first_translation_s,"final_asr_s":final_asr_s,
                "final_translation_s":final_translation_s,"scheduler":r.partial_schedule.value(),
                "translator":r.translator.value(false),"requests":requests,"events":events,
                "final_record":r.wire_record(record)}));
            r.finish();
            // Preserve completed conditions even if a later condition fails.
            std::fs::write(&output, serde_json::to_vec_pretty(&json!({"backend":backend,"threads":8,
                "model_sha256":model_hash,"wav_sha256":wav_hash,"translation_model":translation_model,
                "first_request_s":first_s,"rounds_requested":rounds,"quality_gate_passed":false,
                "note":"Real native ASR and HTTP, production scheduler; known file end. No VAD/capture/IPC client/UI/game isolation. Setup excluded; no ASR warmup.","runs":reports})).unwrap()).unwrap();
            println!("paced {backend}, round {round}, interval {interval_s}: first translation {first_translation_s:?}, final {final_translation_s:.3} s");
        }
    }
}

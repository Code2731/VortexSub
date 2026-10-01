//! File-only feasibility probe. No production capture or trimming policy changes.
use echosub_asr_whisper::{AsrEngine, Cancellation, DecodeOutcome, Segment};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::time::Instant;

fn decode(engine: &mut AsrEngine, pcm: &[f32]) -> (Vec<Segment>, f64) {
    let start = Instant::now();
    let DecodeOutcome::Completed(segments) = engine
        .transcribe_cancellable_timed(pcm, "en", &Cancellation::default())
        .unwrap()
    else {
        panic!("unexpected cancellation")
    };
    (segments, start.elapsed().as_secs_f64())
}

#[test]
#[ignore = "existing model and English WAV required; explicitly invoked by file probe"]
fn native_timed_trim_probe() {
    let model = std::env::var("ECHOSUB_SCHEDULE_MODEL").unwrap();
    let wav = std::env::var("ECHOSUB_SCHEDULE_WAV").unwrap();
    let report = std::env::var("ECHOSUB_SCHEDULE_REPORT").unwrap();
    let prefix = "We should take the left path.";
    let model_hash = format!("{:x}", Sha256::digest(std::fs::read(&model).unwrap()));
    let wav_hash = format!("{:x}", Sha256::digest(std::fs::read(&wav).unwrap()));
    let pcm = crate::native_owner::load_wav(&wav, &wav_hash).unwrap();
    assert!(
        pcm.len() > 64000 && pcm.len() <= 128000,
        "use the existing 7.605 s en-joined fixture"
    );
    let gpu = std::env::var("ECHOSUB_SCHEDULE_BACKEND").is_ok_and(|s| s == "cuda");
    let dtw = std::env::var("ECHOSUB_SCHEDULE_DTW").is_ok_and(|s| s == "1");
    let mut engine = if dtw {
        AsrEngine::load_dtw_base(&model, gpu, 8)
    } else {
        AsrEngine::load(&model, gpu, 8)
    }
    .unwrap();
    let mut observations = Vec::new();
    let mut aligned = Vec::new();
    for end in [48000, 64000] {
        let (segments, decode_s) = decode(&mut engine, &pcm[..end]);
        let align = if dtw {
            crate::decode_window::prefix_dtw
        } else {
            crate::decode_window::prefix
        };
        let candidate = align(
            &segments,
            echosub_audio_core::SampleRange {
                start: 0,
                end: end as u64,
            },
            prefix,
        );
        observations.push(json!({"input_s":end as f64/16000.,"decode_s":decode_s,
            "candidate_boundary_kind":if dtw {"DtwLexicalLandmark"} else {"TokenIntervalEnd"},"candidate_end_s":candidate.as_ref().ok().map(|p|p.end as f64/16000.),"rejected_reason":candidate.as_ref().err(),"segments":segments.iter().map(|s|json!({"start_s":s.start_ms as f64/1000.,"end_s":s.end_ms as f64/1000.,"text":s.text,"tokens":s.tokens.iter().map(|t|json!({"byte_start":t.byte_start,"byte_end":t.byte_end,"start_s":t.start_ms as f64/1000.,"end_s":t.end_ms as f64/1000.,"dtw_s":t.dtw_ms.map(|ms|ms as f64/1000.)})).collect::<Vec<_>>()})).collect::<Vec<_>>()}));
        aligned.push(candidate.ok());
    }
    let (full, full_s) = decode(&mut engine, &pcm);
    let product = echosub_audio_core::SampleRange {
        start: 0,
        end: pcm.len() as u64,
    };
    let window = aligned[0]
        .as_ref()
        .zip(aligned[1].as_ref())
        .and_then(|(a, b)| crate::decode_window::window(a, b, product));
    let cut = window.map(|w| w.start as usize);
    let trimmed = cut.map(|cut| {
        let (segments, decode_s) = decode(&mut engine, &pcm[cut..]);
        let merged = crate::decode_window::merge(aligned[1].as_ref().unwrap(), &segments, window.unwrap());
        json!({"merged_source":merged.as_ref().ok(),"merge_rejected_reason":merged.as_ref().err(),"cut_s":cut as f64/16000.,"input_s":(pcm.len()-cut) as f64/16000.,
            "decode_s":decode_s,"source":segments.iter().map(|s|s.text.as_str()).collect::<String>().trim()})
    });
    let result = json!({"model_sha256":model_hash,"wav_sha256":wav_hash,"backend":if gpu {"cuda"} else {"cpu"},"threads":8,"dtw":dtw,
        "prefix":prefix,"observations":observations,"full_input_s":pcm.len() as f64/16000.,
        "full_decode_s":full_s,"full_source":full.iter().map(|s|s.text.as_str()).collect::<String>().trim(),
        "trimmed":trimmed,"live_enabled":false,"quality_gate_passed":false,
        "note":"File-only single observations, no VAD/HTTP/UI. Missing or inconsistent metadata rejects cut. Trimmed output is not merged into production source; merge requires exact timed overlap; rejected candidates keep the full window."});
    std::fs::write(report, serde_json::to_vec_pretty(&result).unwrap()).unwrap();
    println!(
        "trim candidate available: {}; full decode: {:.3} s",
        cut.is_some(),
        full_s
    );
}

#[test]
#[ignore = "explicit existing-model file probe; forces an invalid overlap on the real owner"]
fn native_window_fallback_probe() {
    use crate::native_owner::{Completion, Decode, ModelConfig, NativeOwner};
    use echosub_audio_core::*;
    use echosub_pipeline_core::{AsrKind, Outcome, Pipeline};
    let model = std::env::var("ECHOSUB_SCHEDULE_MODEL").unwrap();
    let wav = std::env::var("ECHOSUB_SCHEDULE_WAV").unwrap();
    let hash = |p: &str| format!("{:x}", Sha256::digest(std::fs::read(p).unwrap()));
    let pcm = crate::native_owner::load_wav(&wav, &hash(&wav)).unwrap();
    let gpu = std::env::var("ECHOSUB_SCHEDULE_BACKEND").as_deref() == Ok("cuda");
    let mut engine = AsrEngine::load_dtw_base(&model, gpu, 8).unwrap();
    let (segments, _) = decode(&mut engine, &pcm[..64000]);
    let prefix = crate::decode_window::prefix_dtw(
        &segments,
        SampleRange {
            start: 0,
            end: 64000,
        },
        "We should take the left path.",
    )
    .unwrap();
    drop(engine);
    let owner = NativeOwner::start(ModelConfig {
        path: model.clone(),
        hash: hash(&model),
        gpu,
        threads: 8,
        vad: None,
        decode_window: true,
        pad_short_partials: false,
    });
    assert!(matches!(
        owner
            .completed
            .recv_timeout(std::time::Duration::from_secs(30))
            .unwrap(),
        Completion::Ready { .. }
    ));
    let audio = AudioIdentity {
        session_id: 1,
        epoch: 1,
    };
    let mut ring = RollingAudio::new(MAX_ROLLING_SAMPLES, audio, 0).unwrap();
    ring.append(audio, 0, &pcm).unwrap();
    let mut pool = SnapshotPool::new(4, MAX_SNAPSHOT_SAMPLES).unwrap();
    let mut core = Pipeline::new_asr_only(audio, 1000).unwrap();
    core.submit_asr(
        SegmentIdentity {
            audio,
            segment_id: 1,
        },
        SampleRange {
            start: 0,
            end: pcm.len() as u64,
        },
        AsrKind::Partial,
        &ring,
        &mut pool,
        0,
    )
    .unwrap();
    let job = core.next_asr().unwrap();
    let key = job.key();
    // Cut after the matching words: the real merge must fail, then full PCM must survive.
    owner
        .jobs
        .send(Decode {
            job,
            language: "en".into(),
            cancellation: Cancellation::default(),
            continued_from: None,
            window: Some(crate::window_state::Plan {
                prefix,
                start: 32000,
            }),
        })
        .unwrap();
    let completion = owner
        .completed
        .recv_timeout(std::time::Duration::from_secs(30))
        .unwrap();
    if let Completion::Decoded {
        outcome,
        window_attempted,
        window_fallback,
        ..
    } = completion
    {
        assert!(window_attempted && window_fallback);
        let Outcome::Text(text) = outcome else {
            panic!("fallback did not return full text")
        };
        assert_eq!(text,"We should take the left path. There are three enemies near the gate. Do not open the door until I return.");
        core.complete_asr(key, Outcome::Text(text), 1).unwrap();
        std::fs::write(std::env::var("ECHOSUB_SCHEDULE_REPORT").unwrap(),serde_json::to_vec_pretty(&json!({"window_attempted":true,"window_fallback":true,"complete_source_matches_fixture":true,"backend":if gpu {"cuda"} else {"cpu"},"capture":false})).unwrap()).unwrap();
    } else {
        panic!("missing owner completion")
    }
    owner.finish();
}

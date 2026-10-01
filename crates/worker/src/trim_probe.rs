//! File-only feasibility probe. No production capture or trimming policy changes.
use echosub_asr_whisper::{AsrEngine, Cancellation, DecodeOutcome, Segment};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::time::Instant;

fn boundary(segments: &[Segment], prefix: &str, samples: usize) -> Result<usize, &'static str> {
    let raw: String = segments.iter().map(|s| s.text.as_str()).collect();
    if raw.len() > 4096 || !raw.trim_start().starts_with(prefix) {
        return Err("PrefixMismatchOrOversizedText");
    }
    let target = raw.len() - raw.trim_start().len() + prefix.len();
    let mut base = 0;
    let mut covered = 0;
    let mut end_ms = 0;
    for segment in segments {
        for token in &segment.tokens {
            let start = base + token.byte_start;
            let end = base + token.byte_end;
            if start != covered
                || end <= start
                || token.byte_end > segment.text.len()
                || end > target
            {
                return Err("IncompleteOrCrossingTokenCoverage");
            }
            let piece = raw.get(start..end).ok_or("UnsafeUtf8Boundary")?;
            let point = piece
                .chars()
                .all(|c| c.is_whitespace() || c.is_ascii_punctuation());
            if token.start_ms < end_ms
                || token.end_ms < token.start_ms
                || token.end_ms == token.start_ms && !point
            {
                return Err("InvalidOrZeroLengthWordTime");
            }
            covered = end;
            end_ms = token.end_ms;
            if covered == target {
                let sample = usize::try_from(end_ms)
                    .ok()
                    .and_then(|s| s.checked_mul(16))
                    .ok_or("TimeOverflow")?;
                return if sample > 0 && sample < samples {
                    Ok(sample)
                } else {
                    Err("TimeOutsidePcm")
                };
            }
        }
        base += segment.text.len();
    }
    Err("MissingTokenCoverage")
}

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
    let mut engine = AsrEngine::load(&model, false, 8).unwrap();
    let mut observations = Vec::new();
    let mut candidates = Vec::new();
    for end in [48000, 64000] {
        let (segments, decode_s) = decode(&mut engine, &pcm[..end]);
        let candidate = boundary(&segments, prefix, end);
        candidates.push(candidate.ok());
        observations.push(json!({"input_s":end as f64/16000.,"decode_s":decode_s,
            "candidate_end_s":candidate.ok().map(|s|s as f64/16000.),"rejected_reason":candidate.err(),"segments":segments.iter().map(|s|json!({"start_s":s.start_ms as f64/1000.,"end_s":s.end_ms as f64/1000.,"text":s.text,"tokens":s.tokens.iter().map(|t|json!({"byte_start":t.byte_start,"byte_end":t.byte_end,"start_s":t.start_ms as f64/1000.,"end_s":t.end_ms as f64/1000.})).collect::<Vec<_>>()})).collect::<Vec<_>>()}));
    }
    let (full, full_s) = decode(&mut engine, &pcm);
    // Two consistent exact-text boundaries; keep 0.3 s as a diagnostic overlap.
    let cut = match (candidates[0], candidates[1]) {
        (Some(a), Some(b)) if a.abs_diff(b) <= 2560 => a.min(b).checked_sub(4800),
        _ => None,
    }
    .filter(|cut| *cut > 0 && pcm.len() - cut >= 16000);
    let trimmed = cut.map(|cut| {
        let (segments, decode_s) = decode(&mut engine, &pcm[cut..]);
        json!({"cut_s":cut as f64/16000.,"input_s":(pcm.len()-cut) as f64/16000.,
            "decode_s":decode_s,"source":segments.iter().map(|s|s.text.as_str()).collect::<String>().trim()})
    });
    let result = json!({"model_sha256":model_hash,"wav_sha256":wav_hash,"backend":"cpu","threads":8,
        "prefix":prefix,"observations":observations,"full_input_s":pcm.len() as f64/16000.,
        "full_decode_s":full_s,"full_source":full.iter().map(|s|s.text.as_str()).collect::<String>().trim(),
        "trimmed":trimmed,"live_enabled":false,"quality_gate_passed":false,
        "note":"File-only single observations, no VAD/HTTP/UI. Missing or inconsistent metadata rejects cut. Trimmed output is not merged into production source; retained overlap may duplicate a fragment."});
    std::fs::write(report, serde_json::to_vec_pretty(&result).unwrap()).unwrap();
    println!(
        "trim candidate available: {}; full decode: {:.3} s",
        cut.is_some(),
        full_s
    );
}

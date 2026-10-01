//! File-only whole/prefix comparison. No capture, VAD, or live scheduling.
use crate::corpus;
use echosub_asr_whisper::{AsrEngine, ENGINE_ID};
use serde_json::json;
use std::{error::Error, path::Path, time::Instant};

pub fn run(args: &[String]) -> Result<(), Box<dyn Error>> {
    let fixtures = Path::new(&args[0]);
    let model = Path::new(&args[1]);
    let expected_hash = &args[2];
    let output = Path::new(&args[3]);
    let threads: i32 = args[4].parse()?;
    let backend = &args[5];
    let rounds: usize = args[6].parse()?;
    let pad_short = match args.get(7).map(String::as_str) {
        None => false,
        Some("pad-short") => true,
        _ => return Err("optional prefix argument must be pad-short".into()),
    };
    if !(1..=64).contains(&threads) || !(1..=20).contains(&rounds) {
        return Err("threads must be 1..64; rounds must be 1..20".into());
    }
    let gpu = match backend.as_str() {
        "cpu" => false,
        "cuda" if cfg!(feature = "cuda") => true,
        _ => return Err("backend must be cpu or compiled cuda".into()),
    };
    if corpus::sha256(model)? != *expected_hash {
        return Err("model SHA256 mismatch".into());
    }
    let inputs = corpus::load(fixtures)?;
    if inputs.iter().any(|input| input.pcm.len() > 16000 * 60) {
        return Err("diagnostic inputs are limited to 60 seconds".into());
    }
    let load_start = Instant::now();
    let mut engine = AsrEngine::load(
        model.to_str().ok_or("model path must be UTF-8")?,
        gpu,
        threads,
    )?;
    let load_s = load_start.elapsed().as_secs_f64();
    let mut results = Vec::new();
    for round in 0..rounds {
        let modes = if round % 2 == 0 {
            ["full", "prefix"]
        } else {
            ["prefix", "full"]
        };
        for input in &inputs {
            for mode in modes {
                let mut ends = if mode == "prefix" {
                    (12800..input.pcm.len()).step_by(4096).collect::<Vec<_>>()
                } else {
                    Vec::new()
                };
                ends.push(input.pcm.len());
                let mut observations = Vec::new();
                let mut previous = String::new();
                let mut available_s = 0.0_f64;
                let mut revisions = 0;
                for end in ends {
                    let start = Instant::now();
                    let mut padded = Vec::new();
                    let pcm = if pad_short && end < 16000 {
                        padded.extend_from_slice(&input.pcm[..end]);
                        // whisper.cpp discards one 10 ms mel frame; 1.0 s still
                        // becomes 990 ms and fails its minimum input check.
                        padded.resize(16320, 0.0);
                        padded.as_slice()
                    } else {
                        &input.pcm[..end]
                    };
                    let segments = engine.transcribe(pcm, &input.fixture.language)?;
                    let text = segments.iter().map(|s| s.text.as_str()).collect::<String>();
                    let decode_s = start.elapsed().as_secs_f64();
                    available_s = available_s.max(end as f64 / 16000.0) + decode_s;
                    let common = previous
                        .chars()
                        .zip(text.chars())
                        .take_while(|(a, b)| a == b)
                        .count();
                    let removed = previous.chars().count() - common;
                    if removed > 0 {
                        revisions += 1;
                    }
                    observations.push(json!({"audio_end_s":end as f64/16000.0,"decode_s":decode_s,
                        "simulated_available_s":available_s,"text":text,"shared_prefix_chars":common,"removed_chars":removed}));
                    previous = text;
                }
                eprintln!(
                    "round={} {} {}: {}",
                    round + 1,
                    input.fixture.id,
                    mode,
                    previous
                );
                results.push(json!({"fixture_id":input.fixture.id,"language":input.fixture.language,
                    "kind":input.fixture.kind,"round":round+1,"mode":mode,"audio_s":input.pcm.len() as f64/16000.0,
                    "reference":input.fixture.reference,"revision_events":revisions,"observations":observations}));
            }
        }
    }
    if let Some(parent) = output.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let candidate = if pad_short {
        "whisper-base-pad-short"
    } else {
        "whisper-base"
    };
    let report = json!({"schema_version":1,"candidate":candidate,"engine":ENGINE_ID,"pad_short":pad_short,"pad_short_target_s":1.02,
        "platform":std::env::consts::OS,"provider":backend,"threads":threads,"load_s":load_s,
        "model_sha256":expected_hash,"fixture_manifest_sha256":corpus::sha256(fixtures)?,
        "timing_scope":"file decode and sequential prefix simulation; not live latency","results":results});
    std::fs::write(output, serde_json::to_vec_pretty(&report)?)?;
    Ok(())
}

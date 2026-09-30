//! Fixed-arrival diagnostic load; one running request, no pending queue.
use crate::corpus;
use echosub_asr_whisper::AsrEngine;
use serde_json::{json, Value};
use std::error::Error;
use std::path::Path;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

fn utc_s() -> f64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_secs_f64()
}

pub fn run(args: &[String]) -> Result<(), Box<dyn Error>> {
    let fixtures = Path::new(&args[0]);
    let catalog_path = Path::new(&args[1]);
    let output = Path::new(&args[2]);
    let threads: i32 = args[3].parse()?;
    let backend = &args[4];
    let id = &args[5];
    let duration_s: f64 = args[6].parse()?;
    let interval_s: f64 = args[7].parse()?;
    if !duration_s.is_finite()
        || !(1.0..=3600.0).contains(&duration_s)
        || !interval_s.is_finite()
        || !(0.05..=10.0).contains(&interval_s)
    {
        return Err("load duration must be 1..3600 s and interval 0.05..10 s".into());
    }
    let gpu = match backend.as_str() {
        "cpu" => false,
        "cuda" if cfg!(feature = "cuda") => true,
        "metal" if cfg!(feature = "metal") => true,
        _ => return Err("backend must match the compiled feature".into()),
    };
    let inputs = corpus::load(fixtures)?;
    let inputs: Vec<_> = inputs
        .iter()
        .filter(|x| x.fixture.kind != "silence")
        .collect();
    if inputs.is_empty() {
        return Err("no speech fixtures".into());
    }
    let catalog: Value = serde_json::from_slice(&std::fs::read(catalog_path)?)?;
    if catalog["schema_version"] != 1 {
        return Err("unsupported model catalog".into());
    }
    let model = catalog["models"]
        .as_array()
        .ok_or("missing models")?
        .iter()
        .find(|m| m["id"] == *id && m["role"] == "asr")
        .ok_or("unknown ASR model")?;
    let model_path = catalog_path
        .parent()
        .unwrap_or(Path::new("."))
        .join(model["path"].as_str().ok_or("missing path")?);
    if corpus::sha256(&model_path)? != model["sha256"].as_str().ok_or("missing hash")? {
        return Err("model hash mismatch".into());
    }
    let load = Instant::now();
    let mut engine = AsrEngine::load(
        model_path.to_str().ok_or("model path not UTF-8")?,
        gpu,
        threads,
    )?;
    let load_s = load.elapsed().as_secs_f64();
    let warm = Instant::now();
    engine.transcribe(&inputs[0].pcm, &inputs[0].fixture.language)?;
    let warm_up_s = warm.elapsed().as_secs_f64();
    let ready = Path::new(&args[8]);
    let gate = Path::new(&args[9]);
    if ready.exists() || gate.exists() {
        return Err("load control files must be new".into());
    }
    std::fs::write(ready, "ready")?;
    let wait = Instant::now();
    while !gate.exists() {
        if wait.elapsed().as_secs() >= 120 {
            return Err("start gate timed out".into());
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    let start_utc_s: f64 = std::fs::read_to_string(gate)?.trim().parse()?;
    if !start_utc_s.is_finite() || (start_utc_s - utc_s()).abs() > 30.0 {
        return Err("invalid start gate time".into());
    }
    while utc_s() < start_utc_s {
        std::thread::sleep(Duration::from_millis(1));
    }
    let actual_start_utc_s = utc_s();
    let clock = Instant::now();
    let offered = (duration_s / interval_s).ceil() as usize;
    let mut next = 0usize;
    let mut skipped = 0usize;
    let mut runs = Vec::new();
    while clock.elapsed().as_secs_f64() < duration_s {
        let now = clock.elapsed().as_secs_f64();
        let due = (now / interval_s).floor() as usize;
        if due >= offered {
            break;
        }
        if due < next {
            std::thread::sleep(Duration::from_millis(1));
            continue;
        }
        skipped += due - next;
        next = due + 1;
        let input = inputs[due % inputs.len()];
        let begin = clock.elapsed().as_secs_f64();
        let timer = Instant::now();
        let result = engine.transcribe(&input.pcm, &input.fixture.language);
        let decode_s = timer.elapsed().as_secs_f64();
        let (segments, error) = match result {
            Ok(s) => (Some(s), None),
            Err(e) => (None, Some(e.to_string())),
        };
        runs.push(json!({"arrival_index":due,"fixture_id":input.fixture.id,"scheduled_s":due as f64*interval_s,"start_s":begin,"finish_s":clock.elapsed().as_secs_f64(),"start_lag_s":begin-due as f64*interval_s,"decode_s":decode_s,"audio_s":input.pcm.len() as f64/16000.0,"segment_count":segments.as_ref().map(Vec::len),"error":error}));
        if runs.len() % 100 == 0 {
            eprintln!(
                "{id}: {:.1} s / {duration_s}, {} finished, {skipped} skipped",
                clock.elapsed().as_secs_f64(),
                runs.len()
            );
        }
    }
    skipped += offered.saturating_sub(next);
    let report = json!({"task":"T00-04.4","model_id":id,"model_sha256":model["sha256"],"fixtures_sha256":corpus::sha256(fixtures)?,"backend_requested":backend,"threads":threads,"load_s":load_s,"warm_up_s":warm_up_s,"duration_s":duration_s,"actual_wall_s":clock.elapsed().as_secs_f64(),"scheduled_start_utc_s":start_utc_s,"actual_start_utc_s":actual_start_utc_s,"actual_end_utc_s":utc_s(),"interval_s":interval_s,"offered":offered,"skipped":skipped,"pending_capacity":0,"policy":"one running request; skip expired arrivals; fixture selected by arrival index","runs":runs});
    std::fs::write(output, serde_json::to_vec_pretty(&report)?)?;
    Ok(())
}

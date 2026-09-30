use crate::{corpus, scoring};
use echosub_asr_whisper::{AsrEngine, ENGINE_ID};
use serde::Deserialize;
use serde_json::{json, Value};
use std::error::Error;
use std::path::Path;
use std::time::Instant;

#[derive(Deserialize)]
struct ModelManifest {
    schema_version: u32,
    models: Vec<Model>,
}
#[derive(Deserialize)]
struct Model {
    id: String,
    role: String,
    path: String,
    sha256: String,
}

fn memory() -> Value {
    #[cfg(windows)]
    unsafe {
        use windows::Win32::System::{
            ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX},
            Threading::GetCurrentProcess,
        };
        let mut counters = PROCESS_MEMORY_COUNTERS_EX::default();
        counters.cb = std::mem::size_of_val(&counters) as u32;
        if GetProcessMemoryInfo(
            GetCurrentProcess(),
            &mut counters as *mut _ as *mut _,
            counters.cb,
        )
        .is_ok()
        {
            return json!({"process_peak_working_set_bytes": counters.PeakWorkingSetSize, "process_private_bytes": counters.PrivateUsage, "gpu_peak_bytes": null});
        }
    }
    json!({"process_peak_working_set_bytes": null, "gpu_peak_bytes": null})
}

pub fn run(
    fixtures: &Path,
    models_path: &Path,
    output: &Path,
    threads: i32,
    backend: &str,
) -> Result<(), Box<dyn Error>> {
    let gpu = match backend {
        "cpu" => false,
        "cuda" if cfg!(feature = "cuda") => true,
        "metal" if cfg!(feature = "metal") => true,
        _ => {
            return Err("backend must be cpu or the explicitly compiled cuda/metal feature".into())
        }
    };
    let inputs = corpus::load(fixtures)?;
    let manifest: ModelManifest = serde_json::from_slice(&std::fs::read(models_path)?)?;
    if manifest.schema_version != 1 {
        return Err("model manifest version must be 1".into());
    }
    let mut results = Vec::new();
    for model in manifest.models.iter().filter(|model| model.role == "asr") {
        let model_path = models_path
            .parent()
            .unwrap_or(Path::new("."))
            .join(&model.path);
        if corpus::sha256(&model_path)? != model.sha256 {
            return Err(format!("{}: model hash mismatch", model.id).into());
        }
        eprintln!("Loading {} ({backend})", model.id);
        let load_start = Instant::now();
        let mut engine = AsrEngine::load(
            model_path.to_str().ok_or("model path is not UTF-8")?,
            gpu,
            threads,
        )?;
        let load_s = load_start.elapsed().as_secs_f64();
        let warm_start = Instant::now();
        engine.transcribe(&inputs[0].pcm, &inputs[0].fixture.language)?;
        let warm_s = warm_start.elapsed().as_secs_f64();
        let mut runs = Vec::new();
        for input in &inputs {
            let start = Instant::now();
            let segments = engine.transcribe(&input.pcm, &input.fixture.language)?;
            let decode_s = start.elapsed().as_secs_f64();
            let text = segments
                .iter()
                .map(|segment| segment.text.as_str())
                .collect::<String>();
            let score = scoring::score(&input.fixture.language, &input.fixture.reference, &text);
            let duration_s = input.pcm.len() as f64 / 16000.0;
            eprintln!(
                "{} / {}: {:.6} s, {} {:.3?}",
                model.id, input.fixture.id, decode_s, score.metric, score.error_rate
            );
            runs.push(json!({"fixture_id":input.fixture.id,"fixture_sha256":input.fixture.sha256,"kind":input.fixture.kind,"language":input.fixture.language,"reference":input.fixture.reference,"text":text,"score":score,"decode_s":decode_s,"audio_s":duration_s,"rtf":decode_s/duration_s,"segments":segments.iter().map(|s| json!({"start_s":s.start_ms as f64/1000.0,"end_s":s.end_ms as f64/1000.0,"text":s.text})).collect::<Vec<_>>(),"memory":memory()}));
        }
        results.push(json!({"model_id":model.id,"model_sha256":model.sha256,"load_s":load_s,"warm_up_s":warm_s,"runs":runs}));
    }
    if results.is_empty() {
        return Err("model manifest has no ASR models".into());
    }
    if let Some(parent) = output.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let report = json!({"task":"T00-04.1","engine":ENGINE_ID,"platform":std::env::consts::OS,"arch":std::env::consts::ARCH,"backend_requested":backend,"backend_fallback_verified":false,"threads":threads,"fixtures_manifest_sha256":corpus::sha256(fixtures)?,"model_manifest_sha256":corpus::sha256(models_path)?,"normalization":"Unicode lowercase, remove punctuation; English whitespace tokens; ja/ko Unicode scalar values excluding whitespace; no NFKC/NFC","note":"Load and decode timings are not end-to-end caption latency. Native backend diagnostics must confirm actual backend. Synthetic TTS is diagnostic data, not natural-speech quality evidence.","results":results});
    std::fs::write(output, serde_json::to_vec_pretty(&report)?)?;
    Ok(())
}

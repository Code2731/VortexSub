//! Standalone authored-fixture diagnostic for the HTTP owner.
use echosub_audio_core::{AudioIdentity, JobIdentity};
use echosub_pipeline_core::{TranslationJob, TranslationKey};
use echosub_translation::{
    http::select_model,
    owner::{Output, Owner},
    Endpoint, Prepared,
};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{
    collections::HashSet,
    fs,
    io::Read,
    time::{Duration, Instant},
};

fn main() {
    if let Err(error) = run() {
        eprintln!("translation probe: {error}");
        std::process::exit(1);
    }
}
fn poll(owner: &mut Owner) -> Result<echosub_translation::owner::Completion, String> {
    let start = Instant::now();
    loop {
        if let Some(result) = owner.poll() {
            return Ok(result);
        }
        if start.elapsed() > Duration::from_secs(10) {
            owner.cancel();
            return Err("HTTP owner did not return".into());
        }
        std::thread::sleep(Duration::from_millis(1));
    }
}
fn run() -> Result<(), String> {
    let args: Vec<_> = std::env::args().collect();
    if !matches!(args.len(), 5 | 7) {
        return Err(
            "Usage: <http://127.0.0.1:PORT/v1/> <model-id or -> <fixtures.json> <report.json> [warmup-rounds measured-rounds]"
                .into(),
        );
    }
    let warmup: usize = args
        .get(5)
        .map_or(Ok(0), |n| n.parse())
        .map_err(|_| "Invalid warmup")?;
    let rounds: usize = args
        .get(6)
        .map_or(Ok(1), |n| n.parse())
        .map_err(|_| "Invalid rounds")?;
    if warmup > 5 || !(1..=10).contains(&rounds) {
        return Err("Expected warmup 0..5 and measured rounds 1..10".into());
    }
    let endpoint = Endpoint::parse(&args[1]).map_err(|e| format!("endpoint: {e:?}"))?;
    let mut bytes = Vec::new();
    fs::File::open(&args[3])
        .map_err(|_| "Cannot open fixtures")?
        .take(1024 * 1024 + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| "Cannot read fixtures")?;
    if bytes.len() > 1024 * 1024 {
        return Err("Fixtures exceed 1 MiB".into());
    }
    let fixtures: Value = serde_json::from_slice(&bytes).map_err(|_| "Invalid fixture JSON")?;
    let fixture_sha256 = format!("{:x}", Sha256::digest(&bytes));
    if fixtures["schema_version"] != 1 {
        return Err("Unsupported fixture schema".into());
    }
    let cases = fixtures["cases"]
        .as_array()
        .filter(|v| !v.is_empty() && v.len() <= 100)
        .ok_or("Expected 1..100 cases")?;
    let mut seen = HashSet::new();
    for case in cases {
        let id = case["id"]
            .as_str()
            .filter(|id| !id.is_empty() && id.len() <= 128)
            .ok_or("Invalid fixture ID")?;
        if !seen.insert(id)
            || !matches!(case["language"].as_str(), Some("en" | "ja" | "ko"))
            || case["source"].as_str().is_none()
        {
            return Err("Invalid or duplicate fixture".into());
        }
        if let Some(context) = case.get("context") {
            if !context
                .as_array()
                .is_some_and(|v| v.len() <= 2 && v.iter().all(Value::is_string))
            {
                return Err("Expected at most two context strings".into());
            }
        }
    }
    let token = std::env::var("ECHOSUB_TRANSLATION_TOKEN").ok();
    let mut owner = Owner::new(endpoint, token.as_deref()).map_err(|e| format!("owner: {e:?}"))?;
    owner
        .models(Duration::from_secs(8))
        .map_err(|e| format!("catalog submit: {e:?}"))?;
    let ids = match poll(&mut owner)?.result {
        Ok(Output::Models(ids)) => ids,
        Err(e) => return Err(format!("catalog: {e:?}")),
        _ => return Err("Unexpected catalog completion".into()),
    };
    let model = select_model(&ids, (args[2] != "-").then_some(args[2].as_str()))
        .map_err(|e| format!("selection: {e:?}"))?;
    let clock = Instant::now();
    let mut results = Vec::new();
    let mut completed = 0;
    let mut warmup_results = Vec::new();
    for round in 0..warmup + rounds {
        for (index, case) in cases.iter().enumerate() {
            let now_ns = u64::try_from(clock.elapsed().as_nanos()).map_err(|_| "Clock overflow")?;
            let job = TranslationJob {
                key: TranslationKey {
                    source: JobIdentity {
                        audio: AudioIdentity {
                            session_id: 1,
                            epoch: 0,
                        },
                        segment_id: index as u64 + 1,
                        source_revision: 1,
                    },
                    request_id: (round * cases.len() + index) as u64 + 1,
                },
                source: case["source"].as_str().unwrap().into(),
                context: case
                    .get("context")
                    .and_then(Value::as_array)
                    .map(|values| {
                        values
                            .iter()
                            .map(|v| v.as_str().unwrap().to_owned())
                            .collect()
                    })
                    .unwrap_or_default(),
                source_language: case["language"].as_str().unwrap().into(),
                target_language: "ko".into(),
                deadline_ns: now_ns.checked_add(8_000_000_000).ok_or("Clock overflow")?,
            };
            let fingerprint = match echosub_translation::prepare(&job, &model, now_ns)
                .map_err(|e| format!("prepare: {e:?}"))?
            {
                Prepared::Send(mut request) => {
                    request.body["model"] = json!("<selected-model>");
                    format!(
                        "{:x}",
                        Sha256::digest(
                            serde_json::to_vec(&request.body)
                                .map_err(|_| "Request fingerprint failed")?
                        )
                    )
                }
                Prepared::Bypass(_) => "bypass".into(),
            };
            let start = Instant::now();
            let (translation, error) = match owner.translate(&job, &model, now_ns) {
                Ok(()) => {
                    let result = poll(&mut owner)?;
                    if result.key != Some(job.key) {
                        return Err("Completion identity mismatch".into());
                    }
                    match result.result {
                        Ok(Output::Text(text)) => (Some(text), None),
                        Ok(Output::Bypass) => (Some(job.source.clone()), None),
                        Err(e) => (None, Some(format!("{e:?}"))),
                        _ => return Err("Unexpected translation completion".into()),
                    }
                }
                Err(e) => (None, Some(format!("{e:?}"))),
            };
            if translation.is_some() && round >= warmup {
                completed += 1;
            }
            let elapsed_s = start.elapsed().as_secs_f64();
            println!(
                "case={} elapsed_s={elapsed_s:.6} completed={} round={round} warmup={}",
                case["id"].as_str().unwrap(),
                translation.is_some(),
                round < warmup
            );
            let row = json!({"id":case["id"],"round":round.saturating_sub(warmup),"language":case["language"],"source":job.source,"context":job.context,"request_sha256":fingerprint,"reference":case["reference"],"translation":translation,"error":error,"elapsed_s":elapsed_s,"manual_review":"PENDING"});
            if round < warmup {
                warmup_results.push(row);
            } else {
                results.push(row);
            }
            let report = json!({"task":"translation-engine-comparison","os":std::env::consts::OS,"fixture_sha256":fixture_sha256,"model_id":model,"available_model_ids":ids,"quality_gate_passed":false,"worker_integrated":false,"context_used":cases.iter().any(|c| c.get("context").and_then(Value::as_array).is_some_and(|v| !v.is_empty())),"cold_first_request_included":warmup==0,"warmup_rounds":warmup,"measured_rounds":rounds,"completed":completed,"offered":cases.len()*rounds,"warmup_results":warmup_results,"results":results});
            fs::write(
                &args[4],
                serde_json::to_vec_pretty(&report).map_err(|_| "Report serialization failed")?,
            )
            .map_err(|_| "Report write failed")?;
        }
    }
    if completed != cases.len() * rounds || warmup_results.iter().any(|r| !r["error"].is_null()) {
        return Err(format!(
            "Completed {completed}/{}; inspect report",
            cases.len() * rounds
        ));
    }
    Ok(())
}

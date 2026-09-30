use crate::corpus;
use echosub_asr_whisper::{AsrEngine, Cancellation, DecodeOutcome, ENGINE_ID};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    error::Error,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{
        mpsc::{self, Receiver, SyncSender},
        Arc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

#[derive(Deserialize)]
struct Catalog {
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

fn gpu(backend: &str) -> Result<bool, Box<dyn Error>> {
    match backend {
        "cpu" => Ok(false),
        "cuda" if cfg!(feature = "cuda") => Ok(true),
        "metal" if cfg!(feature = "metal") => Ok(true),
        _ => Err("backend is unavailable in this executable".into()),
    }
}
fn models(path: &Path) -> Result<Vec<(Model, PathBuf)>, Box<dyn Error>> {
    let catalog: Catalog = serde_json::from_slice(&std::fs::read(path)?)?;
    if catalog.schema_version != 1 {
        return Err("unsupported model catalog".into());
    }
    let mut result = Vec::new();
    let mut ids = std::collections::HashSet::new();
    for model in catalog.models.into_iter().filter(|m| m.role == "asr") {
        if !ids.insert(model.id.clone())
            || model.id.is_empty()
            || model.id.len() > 64
            || !model
                .id
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
        {
            return Err("model IDs must be safe filename components".into());
        }
        let file = path.parent().unwrap_or(Path::new(".")).join(&model.path);
        if corpus::sha256(&file)? != model.sha256 {
            return Err(format!("{}: model hash mismatch", model.id).into());
        }
        result.push((model, file));
    }
    if result.is_empty() {
        return Err("no ASR models".into());
    }
    Ok(result)
}

struct Job {
    id: u64,
    pcm: Arc<[f32]>,
    language: String,
    token: Cancellation,
    returned_marker: Option<PathBuf>,
}
enum Work {
    Decode(Job),
    Stop,
}
struct Finished {
    id: u64,
    start: Instant,
    end: Instant,
    result: Result<DecodeOutcome, String>,
}
enum Event {
    Ready { load_s: f64, owner: String },
    Finished(Finished),
    Released(Instant),
    Failed(String),
}
struct Owner {
    sender: SyncSender<Work>,
    receiver: Receiver<Event>,
    handle: JoinHandle<()>,
}

fn owner(path: PathBuf, use_gpu: bool, threads: i32) -> Owner {
    let (sender, jobs) = mpsc::sync_channel::<Work>(1);
    let (events, receiver) = mpsc::sync_channel(1);
    let handle = thread::spawn(move || {
        let start = Instant::now();
        let mut engine = match AsrEngine::load(path.to_str().unwrap_or(""), use_gpu, threads) {
            Ok(engine) => engine,
            Err(error) => {
                let _ = events.send(Event::Failed(error.to_string()));
                return;
            }
        };
        if events
            .send(Event::Ready {
                load_s: start.elapsed().as_secs_f64(),
                owner: format!("{:?}", thread::current().id()),
            })
            .is_err()
        {
            return;
        }
        while let Ok(Work::Decode(job)) = jobs.recv() {
            let start = Instant::now();
            let result = engine
                .transcribe_cancellable(&job.pcm, &job.language, &job.token)
                .map_err(|e| e.to_string());
            let end = Instant::now();
            if let Some(marker) = job.returned_marker {
                if let Err(error) = std::fs::write(marker, b"full returned") {
                    let _ =
                        events.send(Event::Failed(format!("full-return marker failed: {error}")));
                    break;
                }
            }
            if events
                .send(Event::Finished(Finished {
                    id: job.id,
                    start,
                    end,
                    result,
                }))
                .is_err()
            {
                break;
            }
        }
        // Only this owner drops the model; synchronous native calls have returned.
        drop(engine);
        let _ = events.send(Event::Released(Instant::now()));
    });
    Owner {
        sender,
        receiver,
        handle,
    }
}
fn event(owner: &Owner) -> Result<Event, Box<dyn Error>> {
    match owner.receiver.recv_timeout(Duration::from_secs(60))? {
        Event::Failed(message) => Err(message.into()),
        event => Ok(event),
    }
}
fn ready(owner: &Owner) -> Result<Value, Box<dyn Error>> {
    match event(owner)? {
        Event::Ready { load_s, owner } => Ok(json!({"load_s":load_s,"owner_thread":owner})),
        _ => Err("expected model readiness".into()),
    }
}
fn finished(owner: &Owner, id: u64) -> Result<Finished, Box<dyn Error>> {
    match event(owner)? {
        Event::Finished(run) if run.id == id => Ok(run),
        _ => Err("unexpected job order".into()),
    }
}
fn text(run: &Finished) -> Result<String, Box<dyn Error>> {
    match &run.result {
        Ok(DecodeOutcome::Completed(segments)) => {
            Ok(segments.iter().map(|s| s.text.as_str()).collect())
        }
        Ok(DecodeOutcome::Cancelled { .. }) => Err("recovery decode was cancelled".into()),
        Err(error) => Err(error.clone().into()),
    }
}
fn enqueue(
    owner: &Owner,
    id: u64,
    pcm: &Arc<[f32]>,
    language: &str,
    token: &Cancellation,
) -> Result<(), Box<dyn Error>> {
    owner.sender.send(Work::Decode(Job {
        id,
        pcm: pcm.clone(),
        language: language.into(),
        token: token.clone(),
        returned_marker: None,
    }))?;
    Ok(())
}
fn active(token: &Cancellation, phase: &str) -> Result<(), Box<dyn Error>> {
    let start = Instant::now();
    loop {
        let snapshot = token.snapshot();
        let observed = match phase {
            "encoder_started" => snapshot.encoder_entries > 0,
            "native_abort_check" => snapshot.native_checks > 0,
            _ => return Err("invalid cancellation phase".into()),
        };
        if snapshot.running && observed {
            return Ok(());
        }
        if start.elapsed() > Duration::from_secs(30) {
            return Err(format!("no live native {phase} observed within 30 seconds").into());
        }
        thread::sleep(Duration::from_millis(1));
    }
}
fn cancelled(run: &Finished, token: &Cancellation) -> Result<Value, Box<dyn Error>> {
    let s = token.snapshot();
    match &run.result {
        Ok(DecodeOutcome::Cancelled {
            abort_observed: true,
            native_error,
        }) if !s.running => Ok(
            json!({"native_checks":s.native_checks,"encoder_entries":s.encoder_entries,"abort_observed":true,"native_error":native_error,"output_segments_returned":0}),
        ),
        Err(message) => Err(message.clone().into()),
        _ => Err("request did not produce an acknowledged native cancellation".into()),
    }
}
fn stop(owner: Owner) -> Result<Instant, Box<dyn Error>> {
    owner.sender.send(Work::Stop)?;
    let released = match event(&owner)? {
        Event::Released(at) => at,
        _ => return Err("context release was not observed".into()),
    };
    owner.handle.join().map_err(|_| "owner thread panicked")?;
    Ok(released)
}
fn save(path: &Path, report: &Value) -> Result<(), Box<dyn Error>> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(path, serde_json::to_vec_pretty(report)?)?;
    Ok(())
}

/// Child paths are owned by this probe. Native logs never share readiness files.
fn spawn_child(
    fixtures: &Path,
    catalog: &Path,
    model: &str,
    threads: i32,
    backend: &str,
    mode: &str,
    path: &Path,
) -> Result<Child, Box<dyn Error>> {
    if path.exists()
        || path.with_extension("returned").exists()
        || path.with_extension("native.log").exists()
    {
        return Err("refusing reused child evidence paths; use a fresh output directory".into());
    }
    let log = std::fs::File::create(path.with_extension("native.log"))?;
    let mut command = Command::new(std::env::current_exe()?);
    command.args([
        "cancel-child",
        fixtures.to_str().ok_or("fixture path encoding")?,
        catalog.to_str().ok_or("catalog path encoding")?,
        model,
        &threads.to_string(),
        backend,
        mode,
        path.to_str().ok_or("result path encoding")?,
    ]);
    command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::from(log));
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x08000000); // CREATE_NO_WINDOW
    }
    Ok(command.spawn()?)
}
// Kill-on-drop prevents timeout/error paths from leaving our probe children behind.
struct OwnedChild(Child);
impl Drop for OwnedChild {
    fn drop(&mut self) {
        if matches!(self.0.try_wait(), Ok(None)) {
            let _ = self.0.kill();
        }
        let _ = self.0.wait();
    }
}
fn wait_file(child: &mut OwnedChild, path: &Path) -> Result<Value, Box<dyn Error>> {
    let start = Instant::now();
    loop {
        if path.exists() {
            if let Ok(bytes) = std::fs::read(path) {
                if let Ok(value) = serde_json::from_slice(&bytes) {
                    return Ok(value);
                }
            }
        }
        if let Some(status) = child.0.try_wait()? {
            return Err(format!("child exited before readiness: {status}").into());
        }
        if start.elapsed() > Duration::from_secs(60) {
            return Err("child readiness timed out".into());
        }
        thread::sleep(Duration::from_millis(1));
    }
}

pub fn run(
    fixtures: &Path,
    catalog: &Path,
    output: &Path,
    threads: i32,
    backend: &str,
    iterations: usize,
) -> Result<(), Box<dyn Error>> {
    if !(10..=100).contains(&iterations) {
        return Err("iterations must be 10..100".into());
    }
    let mut report = json!({"task":"T00-04.2","status":"RUNNING","engine":ENGINE_ID,"backend_requested":backend,"native_backend_verified":false,"threads":threads,"iterations_per_phase":iterations,"time_unit":"seconds","fixture_manifest_sha256":corpus::sha256(fixtures)?,"model_catalog_sha256":corpus::sha256(catalog)?,"models":[],"ownership":"PCM Arc owned by each bounded job; one owner thread loads/decodes/drops each context; controller only shares atomic tokens; next job queued immediately after request; enqueue and native-return ordering measured","limitations":["Synthetic repeated speech is lifecycle stress data, not quality evidence","Cooperative cancellation is best effort; no deadline guarantee","Not capture Stop or epoch/UI validation","Force termination is a fresh child process, not context destruction during a call","Native active snapshot and full-return marker bracket forced termination; not an instruction-level trace","Full-return marker absence alone cannot prove the exact machine instruction at process termination","Pre-encoder spectrogram work is not separately instrumented","Mac Metal untested"]});
    save(output, &report)?;
    let result = run_inner(
        fixtures,
        catalog,
        output,
        threads,
        backend,
        iterations,
        &mut report,
    );
    report["status"] = json!(if result.is_ok() {
        "PASS_PROBE_SCOPE"
    } else {
        "FAIL"
    });
    if let Err(error) = &result {
        report["error"] = json!(error.to_string());
    }
    save(output, &report)?;
    result
}

fn run_inner(
    fixtures: &Path,
    catalog: &Path,
    output: &Path,
    threads: i32,
    backend: &str,
    iterations: usize,
    report: &mut Value,
) -> Result<(), Box<dyn Error>> {
    let use_gpu = gpu(backend)?;
    let inputs = corpus::load(fixtures)?;
    let input = inputs
        .iter()
        .find(|i| matches!(i.fixture.kind.as_str(), "speech" | "synthetic_tts"))
        .ok_or("no speech fixture")?;
    let short: Arc<[f32]> = input.pcm.clone().into();
    let long: Arc<[f32]> = short
        .iter()
        .copied()
        .cycle()
        .take(16000 * 120)
        .collect::<Vec<_>>()
        .into();
    let language = &input.fixture.language;
    let child_dir = output.parent().unwrap_or(Path::new(".")).join("children");
    std::fs::create_dir_all(&child_dir)?;
    for (model, path) in models(catalog)? {
        eprintln!("{} / {backend}: cancellation, recovery, shutdown", model.id);
        let worker = owner(path.clone(), use_gpu, threads);
        let readiness = ready(&worker)?;
        enqueue(&worker, 0, &short, language, &Cancellation::default())?;
        let warm = finished(&worker, 0)?;
        let expected = text(&warm)?;
        let mut rows = Vec::new();
        let mut used_token = Cancellation::default();
        for trial in 0..iterations * 2 {
            let phase = if trial < iterations {
                "encoder_started"
            } else {
                "native_abort_check"
            };
            let iteration = trial % iterations;
            let id = 1 + trial as u64 * 2;
            let token = Cancellation::default();
            enqueue(&worker, id, &long, language, &token)?;
            active(&token, phase)?;
            thread::sleep(Duration::from_millis(2));
            let before = token.snapshot();
            if !before.running {
                return Err("native call finished before cancellation request".into());
            }
            let requested = Instant::now();
            token.request();
            // Queue immediately after requesting cancellation, measuring the bracket
            // instead of assuming enqueue always precedes a fast native return.
            let enqueue_begin = Instant::now();
            enqueue(&worker, id + 1, &short, language, &Cancellation::default())?;
            let enqueue_end = Instant::now();
            let cancelled_run = finished(&worker, id)?;
            let proof = cancelled(&cancelled_run, &token)?;
            let recovered = finished(&worker, id + 1)?;
            if recovered.start < cancelled_run.end || text(&recovered)? != expected {
                return Err("decode overlap or recovery text mismatch".into());
            }
            let latency = cancelled_run
                .end
                .checked_duration_since(requested)
                .ok_or("call returned before cancellation request")?
                .as_secs_f64();
            rows.push(json!({"phase":phase,"iteration":iteration+1,"inflight_native_checks_at_request":before.native_checks,"encoder_entries_at_request":before.encoder_entries,"cancel_to_return_s":latency,"cancel_to_next_start_s":recovered.start.duration_since(requested).as_secs_f64(),"return_to_next_start_s":recovered.start.duration_since(cancelled_run.end).as_secs_f64(),"request_to_enqueue_begin_s":enqueue_begin.duration_since(requested).as_secs_f64(),"request_to_enqueue_end_s":enqueue_end.duration_since(requested).as_secs_f64(),"enqueue_completed_before_native_return":enqueue_end<=cancelled_run.end,"recovery_decode_s":recovered.end.duration_since(recovered.start).as_secs_f64(),"recovery_text_matches_warm_up":true,"decode_overlap":false,"cancellation":proof}));
            used_token = token;
            eprintln!(
                "{} / {} / {}: cancel {:.6} s; recovery {:.6} s",
                model.id,
                phase,
                iteration + 1,
                latency,
                recovered.end.duration_since(recovered.start).as_secs_f64()
            );
        }
        let checks_before = used_token.snapshot().native_checks;
        enqueue(&worker, 999, &short, language, &used_token)?;
        let reused = finished(&worker, 999)?;
        if !matches!(reused.result, Err(ref error) if error.contains("token already used"))
            || used_token.snapshot().native_checks != checks_before
        {
            return Err("used cancellation token was accepted or entered native work".into());
        }
        // Pre-cancel performs no native work and does not poison the reused context.
        let token = Cancellation::default();
        token.request();
        enqueue(&worker, 1000, &short, language, &token)?;
        let pre = finished(&worker, 1000)?;
        if !matches!(
            pre.result,
            Ok(DecodeOutcome::Cancelled {
                abort_observed: false,
                ..
            })
        ) || token.snapshot().native_checks != 0
        {
            return Err("pre-cancel entered native work".into());
        }
        enqueue(&worker, 1001, &short, language, &Cancellation::default())?;
        if text(&finished(&worker, 1001)?)? != expected {
            return Err("pre-cancel poisoned context recovery".into());
        }
        stop(worker)?;
        let mut shutdown = Vec::new();
        let mut forced = Vec::new();
        for iteration in 0..iterations {
            let worker = owner(path.clone(), use_gpu, threads);
            let load = ready(&worker)?;
            // Warm-up prevents initialization latency being reported as cancellation.
            enqueue(&worker, 0, &short, language, &Cancellation::default())?;
            let warm = finished(&worker, 0)?;
            if text(&warm)? != expected {
                return Err("fresh context warm-up mismatch".into());
            }
            let token = Cancellation::default();
            enqueue(&worker, 1, &long, language, &token)?;
            active(&token, "encoder_started")?;
            let requested = Instant::now();
            token.request();
            worker.sender.send(Work::Stop)?;
            let run = finished(&worker, 1)?;
            let proof = cancelled(&run, &token)?;
            let released = match event(&worker)? {
                Event::Released(at) => at,
                _ => return Err("shutdown did not release context".into()),
            };
            worker
                .handle
                .join()
                .map_err(|_| "owner panicked during shutdown")?;
            shutdown.push(json!({"phase":"encoder_started","iteration":iteration+1,"initialization":load,"warm_up_s":warm.end.duration_since(warm.start).as_secs_f64(),"cancel_to_return_s":run.end.checked_duration_since(requested).ok_or("shutdown call completed before request")?.as_secs_f64(),"shutdown_to_context_released_s":released.duration_since(requested).as_secs_f64(),"context_released_after_full_return":released>=run.end,"cancellation":proof}));
            let active_file = child_dir.join(format!("{}-{iteration}-active.json", model.id));
            let returned = active_file.with_extension("returned");
            let mut child = OwnedChild(spawn_child(
                fixtures,
                catalog,
                &model.id,
                threads,
                backend,
                "busy",
                &active_file,
            )?);
            let live = wait_file(&mut child, &active_file)?;
            if live["pid"].as_u64() != Some(child.0.id() as u64)
                || live["running"] != json!(true)
                || live["native_checks"].as_u64().unwrap_or(0) == 0
            {
                return Err("child readiness did not identify our active native call".into());
            }
            if child.0.try_wait()?.is_some() || returned.exists() {
                return Err("force child completed before termination request".into());
            }
            let killed = Instant::now();
            child.0.kill()?;
            let exit = child.0.wait()?;
            let kill_s = killed.elapsed().as_secs_f64();
            if returned.exists() {
                return Err("forced termination raced with native completion".into());
            }
            let recovery_file = child_dir.join(format!("{}-{iteration}-recovery.json", model.id));
            let start = Instant::now();
            let mut recovery = OwnedChild(spawn_child(
                fixtures,
                catalog,
                &model.id,
                threads,
                backend,
                "recover",
                &recovery_file,
            )?);
            let result = wait_file(&mut recovery, &recovery_file)?;
            // Readiness means inference completed; bounded exit polling avoids a hang.
            while recovery.0.try_wait()?.is_none() {
                if start.elapsed() > Duration::from_secs(60) {
                    return Err("recovery child exit timed out".into());
                }
                thread::sleep(Duration::from_millis(1));
            }
            if !recovery.0.wait()?.success() || result["text"] != json!(expected) {
                return Err("fresh process recovery failed".into());
            }
            forced.push(json!({"iteration":iteration+1,"native_active_snapshot":live,"full_return_marker_exists":false,"termination_to_process_exit_s":kill_s,"terminated_exit_status":exit.to_string(),"fresh_process_recovery_s":start.elapsed().as_secs_f64(),"recovery":result}));
        }
        report["models"].as_array_mut().unwrap().push(json!({"model_id":model.id,"model_sha256":model.sha256,"fixture_id":input.fixture.id,"fixture_sha256":input.fixture.sha256,"language":language,"stress_pcm":"repeat/crop the verified speech PCM to 120 seconds; not a natural recording","stress_audio_s":120.0,"initialization":readiness,"warm_up_s":warm.end.duration_since(warm.start).as_secs_f64(),"cancel_restart":rows,"used_token_rejected_before_native_work":true,"pre_cancel_native_checks":0,"post_pre_cancel_recovery_matches":true,"normal_shutdown":shutdown,"forced_shutdown_and_fresh_process":forced}));
        save(output, report)?;
    }
    Ok(())
}

pub fn child(
    fixtures: &Path,
    catalog: &Path,
    model_id: &str,
    threads: i32,
    backend: &str,
    mode: &str,
    output: &Path,
) -> Result<(), Box<dyn Error>> {
    let inputs = corpus::load(fixtures)?;
    let input = inputs
        .iter()
        .find(|i| matches!(i.fixture.kind.as_str(), "speech" | "synthetic_tts"))
        .ok_or("no speech fixture")?;
    let (_, path) = models(catalog)?
        .into_iter()
        .find(|(m, _)| m.id == model_id)
        .ok_or("unknown child model")?;
    let short: Arc<[f32]> = input.pcm.clone().into();
    let worker = owner(path, gpu(backend)?, threads);
    let initialization = ready(&worker)?;
    enqueue(
        &worker,
        0,
        &short,
        &input.fixture.language,
        &Cancellation::default(),
    )?;
    let warm = finished(&worker, 0)?;
    if mode == "recover" {
        let result = json!({"initialization":initialization,"decode_s":warm.end.duration_since(warm.start).as_secs_f64(),"text":text(&warm)?});
        stop(worker)?;
        save(output, &result)?;
        return Ok(());
    }
    if mode != "busy" {
        return Err("invalid child mode".into());
    }
    let long: Arc<[f32]> = short
        .iter()
        .copied()
        .cycle()
        .take(16000 * 120)
        .collect::<Vec<_>>()
        .into();
    let token = Cancellation::default();
    worker.sender.send(Work::Decode(Job {
        id: 1,
        pcm: long,
        language: input.fixture.language.clone(),
        token: token.clone(),
        returned_marker: Some(output.with_extension("returned")),
    }))?;
    active(&token, "native_abort_check")?;
    let snapshot = token.snapshot();
    save(
        output,
        &json!({"native_checks":snapshot.native_checks,"encoder_entries":snapshot.encoder_entries,"running":snapshot.running,"pid":std::process::id(),"initialization":initialization}),
    )?;
    let _ = finished(&worker, 1)?;
    stop(worker)?;
    Err("busy child finished naturally; termination probe missed active work".into())
}

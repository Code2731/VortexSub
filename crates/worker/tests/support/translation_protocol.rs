//! Real local HTTP fixtures through worker IPC; source generation is MOCK.
use super::*;
use std::{
    net::TcpListener,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
};

struct Server {
    endpoint: String,
    bodies: Arc<Mutex<Vec<Value>>>,
    stop: Arc<AtomicBool>,
    thread: Option<thread::JoinHandle<()>>,
}
impl Server {
    fn new() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        listener.set_nonblocking(true).unwrap();
        let endpoint = format!("http://{}/v1/", listener.local_addr().unwrap());
        let bodies = Arc::new(Mutex::new(Vec::new()));
        let observed = bodies.clone();
        let stop = Arc::new(AtomicBool::new(false));
        let stopping = stop.clone();
        let thread = thread::spawn(move || {
            while !stopping.load(Ordering::Acquire) {
                let mut socket = match listener.accept() {
                    Ok((s, _)) => s,
                    Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                        thread::sleep(Duration::from_millis(2));
                        continue;
                    }
                    Err(e) => panic!("accept: {e}"),
                };
                socket.set_nonblocking(false).unwrap();
                socket
                    .set_read_timeout(Some(Duration::from_secs(2)))
                    .unwrap();
                let mut bytes = Vec::new();
                let mut buffer = [0; 1024];
                let (headers, body) = loop {
                    let n = socket.read(&mut buffer).unwrap();
                    if n == 0 {
                        break (String::new(), Value::Null);
                    }
                    bytes.extend_from_slice(&buffer[..n]);
                    assert!(bytes.len() < 64 * 1024);
                    if let Some(end) = bytes.windows(4).position(|s| s == b"\r\n\r\n") {
                        let header = String::from_utf8_lossy(&bytes[..end]).to_string();
                        let length = header
                            .lines()
                            .find_map(|line| {
                                line.to_ascii_lowercase()
                                    .strip_prefix("content-length:")
                                    .map(|v| v.trim().parse::<usize>().unwrap())
                            })
                            .unwrap_or(0);
                        if bytes.len() >= end + 4 + length {
                            let body = if length == 0 {
                                Value::Null
                            } else {
                                serde_json::from_slice(&bytes[end + 4..end + 4 + length]).unwrap()
                            };
                            break (header, body);
                        }
                    }
                };
                if headers.is_empty() {
                    continue;
                }
                let (status, body) = if headers.starts_with("GET /v1/models ") {
                    (200, json!({"data":[{"id":"fixture/model"}]}).to_string())
                } else {
                    let payload: Value =
                        serde_json::from_str(body["messages"][1]["content"].as_str().unwrap())
                            .unwrap();
                    observed.lock().unwrap().push(payload.clone());
                    let text = payload["source_text"].as_str().unwrap();
                    if text.starts_with("slow") {
                        thread::sleep(Duration::from_millis(300));
                    }
                    if text == "unauthorized" {
                        (401, "server secret must not appear in IPC".into())
                    } else if text == "broken" {
                        (200, "not JSON".into())
                    } else {
                        (200,json!({"choices":[{"finish_reason":"stop","message":{"content":"번역 완료","tool_calls":null}}]}).to_string())
                    }
                };
                let _ = write!(socket,"HTTP/1.1 {status} Fixture\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",body.len());
            }
        });
        Self {
            endpoint,
            bodies,
            stop,
            thread: Some(thread),
        }
    }
    fn wait_posts(&self, count: usize) {
        let start = Instant::now();
        while self.bodies.lock().unwrap().len() < count {
            assert!(start.elapsed() < Duration::from_secs(3));
            thread::sleep(Duration::from_millis(2));
        }
    }
}
impl Drop for Server {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            thread.join().unwrap();
        }
    }
}
fn start() -> Worker {
    let mut w = Worker::start_args(vec![
        "--mock-pipeline",
        "--mock-session-control",
        "--diagnostic-translation",
    ]);
    let h = w.send(command(
        "hello",
        "hello",
        json!({"client":"TranslationTest","protocol_major":1}),
    ));
    assert_eq!(h["result"]["capabilities"]["translation"], true);
    w
}
fn state(w: &mut Worker) -> Value {
    w.send(command("state", "get_state", json!({})))["result"].clone()
}
fn until(w: &mut Worker, predicate: impl Fn(&Value) -> bool) -> Value {
    let start = Instant::now();
    loop {
        let s = state(w);
        if predicate(&s) {
            return s;
        }
        assert!(start.elapsed() < Duration::from_secs(4), "state={s}");
        thread::sleep(Duration::from_millis(5));
    }
}
fn configure(w: &mut Worker, s: &Server) {
    assert_eq!(
        w.send(command(
            "configure",
            "configure_translation",
            json!({"endpoint":s.endpoint,"model_id":"fixture/model"})
        ))["ok"],
        true
    );
    until(w, |s| s["translator"]["state"] == "Ready");
}
fn session(w: &mut Worker, language: &str) -> String {
    let response=w.send(command("start","start_session",json!({"history_policy":"retain","config":{"source_language":language,"partial_enabled":true}})));
    assert_eq!(response["ok"], true);
    until(w, |s| s["session"]["state"] == "Running");
    response["result"]["session_id"].as_str().unwrap().into()
}
fn source(w: &mut Worker, text: &str, kind: &str) {
    assert_eq!(
        w.send(command(
            "source",
            "mock_segment",
            json!({"source":text,"kind":kind})
        ))["ok"],
        true
    );
}
fn history(w: &mut Worker) -> Value {
    w.send(command("history", "get_history", json!({"limit":4})))["result"]["records"].clone()
}
fn shutdown(mut w: Worker) {
    assert_eq!(
        w.send(command("shutdown", "shutdown", json!({})))["ok"],
        true
    );
    w.finish();
}

#[test]
fn translation_requires_opt_in_and_rejects_nonlocal_configuration() {
    let mut default = Worker::start();
    hello(&mut default);
    assert_eq!(
        default.send(command(
            "configure",
            "configure_translation",
            json!({"endpoint":"http://127.0.0.1:1234"})
        ))["error"]["code"],
        "UNSUPPORTED_CAPABILITY"
    );
    shutdown(default);
    let mut w = start();
    for params in [
        json!({"endpoint":"http://192.168.1.1:1234"}),
        json!({"endpoint":"http://127.0.0.1:1234","token":"secret"}),
        json!({"endpoint":"http://127.0.0.1:1234","model_id":false}),
    ] {
        assert_eq!(
            w.send(command("invalid", "configure_translation", params))["error"]["code"],
            "INVALID_REQUEST"
        );
    }
    assert_eq!(state(&mut w)["translator"]["state"], "Unavailable");
    shutdown(w);
}
#[test]
fn http_final_only_context_history_and_failure_preserve_source() {
    let s = Server::new();
    let mut w = start();
    configure(&mut w, &s);
    session(&mut w, "en");
    source(&mut w, "partial", "partial");
    thread::sleep(Duration::from_millis(40));
    assert!(s.bodies.lock().unwrap().is_empty());
    source(&mut w, "first", "final");
    until(&mut w, |v| v["translator"]["completed_jobs"] == 1);
    source(&mut w, "unauthorized", "final");
    until(&mut w, |v| v["translator"]["completed_jobs"] == 2);
    source(&mut w, "broken", "final");
    until(&mut w, |v| v["translator"]["completed_jobs"] == 3);
    source(&mut w, "recover", "final");
    until(&mut w, |v| v["translator"]["completed_jobs"] == 4);
    let rows = history(&mut w);
    assert_eq!(rows[0]["source"], "first");
    assert_eq!(rows[0]["translation_state"], "Done");
    assert_eq!(rows[1]["source"], "unauthorized");
    assert_eq!(rows[1]["translation_state"], "Failed");
    assert_eq!(rows[2]["source"], "broken");
    assert_eq!(rows[2]["translation_state"], "Failed");
    assert_eq!(rows[3]["translation_state"], "Done");
    assert_eq!(
        s.bodies.lock().unwrap()[3]["context"],
        json!(["unauthorized", "broken"])
    );
    assert!(!w
        .events
        .iter()
        .any(|e| e.to_string().contains("server secret")));
    assert_eq!(
        w.send(command("inject", "mock_translate", json!({})))["error"]["code"],
        "UNSUPPORTED_CAPABILITY"
    );
    shutdown(w);
}
#[test]
fn pause_rejects_late_http_and_resume_has_new_epoch_context() {
    let s = Server::new();
    let mut w = start();
    configure(&mut w, &s);
    let id = session(&mut w, "en");
    source(&mut w, "slow old", "final");
    s.wait_posts(1);
    assert_eq!(
        w.send(command("pause", "pause_session", json!({"session_id":id})))["ok"],
        true
    );
    until(&mut w, |v| v["translator"]["in_flight"] == false);
    let old = history(&mut w);
    assert_eq!(old[0]["translation_state"], "Skipped");
    assert_eq!(old[0]["source"], "slow old");
    assert_eq!(
        w.send(command(
            "resume",
            "resume_session",
            json!({"session_id":id})
        ))["ok"],
        true
    );
    until(&mut w, |v| v["session"]["state"] == "Running");
    source(&mut w, "new", "final");
    until(&mut w, |v| v["translator"]["completed_jobs"] == 2);
    assert_eq!(history(&mut w)[1]["translation_state"], "Done");
    assert_eq!(s.bodies.lock().unwrap()[1]["context"], json!([]));
    assert!(w
        .events
        .iter()
        .any(|e| e["event"] == "translation.completed" && e["payload"]["applied"] == false));
    shutdown(w);
}
#[test]
fn same_language_bypasses_http_and_disable_requires_idle() {
    let s = Server::new();
    let mut w = start();
    configure(&mut w, &s);
    let id = session(&mut w, "ko");
    source(&mut w, "한국어 원문", "final");
    assert_eq!(history(&mut w)[0]["translation_state"], "Bypassed");
    assert!(s.bodies.lock().unwrap().is_empty());
    assert_eq!(
        w.send(command("disable", "disable_translation", json!({})))["error"]["code"],
        "INVALID_STATE"
    );
    w.send(command("stop", "stop_session", json!({"session_id":id})));
    until(&mut w, |v| v["session"]["state"] == "Idle");
    assert_eq!(
        w.send(command("disable", "disable_translation", json!({})))["ok"],
        true
    );
    shutdown(w);
}
#[test]
fn queue_pressure_and_shutdown_do_not_leave_pending_translations() {
    let s = Server::new();
    let mut w = start();
    configure(&mut w, &s);
    let id = session(&mut w, "en");
    source(&mut w, "slow active", "final");
    s.wait_posts(1);
    for text in ["queued one", "queued two", "overflow"] {
        source(&mut w, text, "final");
    }
    assert_eq!(history(&mut w)[3]["translation_reason"], "QueueFull");
    w.send(command("stop", "stop_session", json!({"session_id":id})));
    until(&mut w, |v| v["session"]["state"] == "Idle");
    let rows = history(&mut w);
    assert!(rows
        .as_array()
        .unwrap()
        .iter()
        .all(|r| r["translation_state"] != "Pending" && r["source_state"] == "Final"));
    shutdown(w);
}
#[test]
fn missing_catalog_model_fails_jobs_without_stalling_worker() {
    let s = Server::new();
    let mut w = start();
    w.send(command(
        "configure",
        "configure_translation",
        json!({"endpoint":s.endpoint,"model_id":"missing"}),
    ));
    until(&mut w, |v| v["translator"]["state"] == "Failed");
    session(&mut w, "en");
    source(&mut w, "still transcribed", "final");
    until(&mut w, |v| v["translator"]["completed_jobs"] == 1);
    assert_eq!(history(&mut w)[0]["translation_state"], "Failed");
    assert!(s.bodies.lock().unwrap().is_empty());
    shutdown(w);
}

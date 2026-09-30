use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Child, ChildStdout, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use serde_json::{json, Value};

struct Worker {
    child: Child,
    output: BufReader<ChildStdout>,
    events: Vec<Value>,
}

impl Worker {
    fn start() -> Self {
        Self::start_with(false)
    }
    fn start_with(mock: bool) -> Self {
        Self::start_args(if mock {
            vec!["--mock-pipeline"]
        } else {
            vec![]
        })
    }
    fn start_args(args: Vec<&str>) -> Self {
        let mut child = Command::new(env!("CARGO_BIN_EXE_echosub-worker"))
            .args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .expect("start worker");
        let output = BufReader::new(child.stdout.take().expect("stdout pipe"));
        Self {
            child,
            output,
            events: Vec::new(),
        }
    }

    fn send(&mut self, request: Value) -> Value {
        let input = self.child.stdin.as_mut().expect("stdin pipe");
        writeln!(input, "{request}").expect("write command");
        input.flush().expect("flush command");
        self.response()
    }

    fn response(&mut self) -> Value {
        loop {
            let mut line = String::new();
            self.output.read_line(&mut line).expect("read response");
            assert!(!line.is_empty(), "worker closed without response");
            let response: Value = serde_json::from_str(&line).expect("stdout must be NDJSON");
            assert!(line.len() <= 256 * 1024 + 1);
            if response["kind"] == "event" {
                self.events.push(response);
                continue;
            }
            assert_eq!(response["kind"], "response");
            assert_eq!(response["v"], 1);
            return response;
        }
    }

    fn finish(mut self) {
        let deadline = Instant::now() + Duration::from_secs(3);
        loop {
            if let Some(status) = self.child.try_wait().expect("wait worker") {
                assert!(status.success(), "worker exited with {status}");
                return;
            }
            assert!(Instant::now() < deadline, "worker did not exit");
            thread::sleep(Duration::from_millis(10));
        }
    }
}

impl Drop for Worker {
    fn drop(&mut self) {
        if self.child.try_wait().ok().flatten().is_none() {
            let _ = self.child.kill();
            let _ = self.child.wait();
        }
    }
}

fn command(id: &str, method: &str, params: Value) -> Value {
    json!({"v": 1, "kind": "command", "request_id": id, "method": method, "params": params})
}

fn hello(worker: &mut Worker) {
    let response = worker.send(command(
        "hello",
        "hello",
        json!({"client": "test", "protocol_major": 1}),
    ));
    assert_eq!(response["ok"], true);
    assert_eq!(response["result"]["capabilities"]["system_audio"], false);
    assert_eq!(response["result"]["capabilities"]["vad"], false);
    assert_eq!(response["result"]["capabilities"]["capture_pcm"], false);
    assert_eq!(
        response["result"]["capabilities"]["source_token_alignment"],
        false
    );
    assert_eq!(response["result"]["capabilities"]["live_asr"], false);
    assert_eq!(response["result"]["implementation"], "mock");
}

#[test]
fn live_asr_requires_capture_and_explicit_native_assets() {
    for args in [
        vec!["--live-asr"],
        vec!["--diagnostic-capture", "--live-asr"],
    ] {
        let output = Command::new(env!("CARGO_BIN_EXE_echosub-worker"))
            .args(args)
            .stdin(Stdio::null())
            .output()
            .unwrap();
        assert!(!output.status.success());
        assert!(String::from_utf8_lossy(&output.stderr).contains("Live ASR requires"));
    }
}

#[test]
fn handshake_commands_and_unicode_round_trip() {
    let mut worker = Worker::start();
    hello(&mut worker);

    let nonce = "한국어 日本語 😊\nnext";
    let response = worker.send(command("ping", "ping", json!({"nonce": nonce})));
    assert_eq!(response["request_id"], "ping");
    assert_eq!(response["result"]["nonce"], nonce);

    let response = worker.send(command("state", "get_state", json!({})));
    assert_eq!(response["result"]["session"]["state"], "Idle");
    let capture = &response["result"]["diagnostic_capture"];
    assert_eq!(capture["startup_deadline_s"], 10.);
    assert!(capture["opening_elapsed_s"].is_null());
    assert!(capture["failure_native_phase"].is_null());

    let response = worker.send(command("unavailable", "start_session", json!({})));
    assert_eq!(response["error"]["code"], "UNSUPPORTED_CAPABILITY");

    let response = worker.send(command("stop", "shutdown", json!({})));
    assert_eq!(response["result"]["accepted"], true);
    worker.finish();
}

#[test]
fn bad_messages_do_not_turn_into_success() {
    let mut worker = Worker::start();
    let response = worker.send(json!({
        "v": 2, "kind": "command", "request_id": "bad-v", "method": "hello",
        "params": {"client": "test", "protocol_major": 1}
    }));
    assert_eq!(response["error"]["code"], "PROTOCOL_MISMATCH");

    let response = worker.send(command("pre-hello", "ping", json!({"nonce": "x"})));
    assert_eq!(response["error"]["code"], "INVALID_STATE");

    worker
        .child
        .stdin
        .as_mut()
        .unwrap()
        .write_all(b"not-json\n")
        .unwrap();
    let response = worker.response();
    assert_eq!(response["error"]["code"], "INVALID_REQUEST");
    assert!(response["request_id"].is_null());

    hello(&mut worker);
    let response = worker.send(command(
        "bad-hello",
        "hello",
        json!({"client": "test", "protocol_major": 2}),
    ));
    assert_eq!(response["error"]["code"], "PROTOCOL_MISMATCH");
    worker.send(command("stop", "shutdown", json!({})));
    worker.finish();
}

#[test]
fn oversized_message_is_rejected_and_connection_closes() {
    let mut worker = Worker::start();
    let mut oversized = vec![b'x'; 256 * 1024 + 1];
    oversized.push(b'\n');
    let _ = worker.child.stdin.as_mut().unwrap().write_all(&oversized);
    let response = worker.response();
    assert_eq!(response["error"]["code"], "INVALID_REQUEST");
    let mut extra = String::new();
    worker.output.read_to_string(&mut extra).unwrap();
    assert!(
        extra.is_empty(),
        "unexpected stdout after oversized request"
    );
    worker.finish();
}

#[test]
fn parent_eof_stops_worker() {
    let mut worker = Worker::start();
    hello(&mut worker);
    worker.child.stdin.take();
    worker.finish();
}

#[test]
fn temporarily_slow_reader_recovers_without_stdout_logs() {
    let mut worker = Worker::start();
    let mut input = worker.child.stdin.take().unwrap();
    let writer = thread::spawn(move || {
        writeln!(
            input,
            "{}",
            command(
                "hello",
                "hello",
                json!({"client": "test", "protocol_major": 1})
            )
        )
        .unwrap();
        for index in 0..1000 {
            writeln!(
                input,
                "{}",
                command(&index.to_string(), "ping", json!({"nonce": "x"}))
            )
            .unwrap();
        }
        writeln!(input, "{}", command("stop", "shutdown", json!({}))).unwrap();
        input.flush().unwrap();
    });
    thread::sleep(Duration::from_millis(100));
    for _ in 0..1002 {
        assert_eq!(worker.response()["ok"], true);
    }
    writer.join().unwrap();
    worker.finish();
}

#[test]
fn history_capability_is_empty_by_default_and_mock_requires_opt_in() {
    let mut worker = Worker::start();
    hello(&mut worker);
    let page = worker.send(command("history", "get_history", json!({})));
    assert_eq!(page["result"]["records"], json!([]));
    assert_eq!(page["result"]["last_seq"], 0);
    let denied = worker.send(command("mock", "mock_segment", json!({"source":"fake"})));
    assert_eq!(denied["error"]["code"], "UNSUPPORTED_CAPABILITY");
    for method in [
        "transcribe_fixture",
        "reset_fixture_epoch",
        "start_capture",
        "stop_capture",
        "export_history",
        "start_session",
        "pause_session",
        "resume_session",
        "stop_session",
    ] {
        let denied = worker.send(command("fixture", method, json!({})));
        assert_eq!(denied["error"]["code"], "UNSUPPORTED_CAPABILITY");
    }
    for params in [
        json!({"limit":5}),
        json!({"offset":-1}),
        json!({"expected_version":"0"}),
        json!({"offset":1}),
    ] {
        assert_eq!(
            worker.send(command("invalid", "get_history", params))["ok"],
            false
        );
    }
    worker.send(command("stop", "shutdown", json!({})));
    worker.finish();
}

#[test]
fn uuid_session_controls_preserve_history_and_reject_stale_identity() {
    let mut w = Worker::start_args(vec!["--mock-pipeline", "--mock-session-control"]);
    hello(&mut w);
    let config = json!({"config":{"source_language":"en"},"history_policy":"retain"});
    assert_eq!(
        w.send(command(
            "bad",
            "start_session",
            json!({"config":{"source_language":"xx"},"history_policy":"retain"})
        ))["ok"],
        false
    );
    assert_eq!(
        w.send(command("state", "get_state", json!({})))["result"]["session"]["session_id"],
        Value::Null
    );
    let start = w.send(command("start", "start_session", config.clone()));
    assert_eq!(start["ok"], true);
    assert_eq!(start["result"]["state"], "Preparing");
    let id = start["result"]["session_id"].as_str().unwrap().to_owned();
    assert_eq!(id.len(), 36);
    assert_eq!(
        w.send(command("duplicate", "start_session", config.clone()))["error"]["code"],
        "INVALID_STATE"
    );
    assert_eq!(
        w.send(command("bypass", "stop_capture", json!({})))["error"]["code"],
        "UNSUPPORTED_CAPABILITY"
    );
    assert_eq!(
        w.send(command(
            "source",
            "mock_segment",
            json!({"source":"before pause"})
        ))["ok"],
        true
    );
    let first =
        w.send(command("history", "get_history", json!({})))["result"]["records"][0].clone();
    assert_eq!(first["product_session_id"], id);
    assert!(first["session_audio_start_s"].as_f64().unwrap() >= 0.);
    assert!(w.events.iter().any(
        |e| e["event"] == "source.final" && e["payload"]["record"]["product_session_id"] == id
    ));
    let pause = w.send(command("pause", "pause_session", json!({"session_id":id})));
    assert_eq!(pause["result"]["state"], "Paused");
    assert!(pause["result"]["epoch"].as_u64().unwrap() > first["epoch"].as_u64().unwrap());
    assert_eq!(
        w.send(command(
            "source",
            "mock_segment",
            json!({"source":"paused"})
        ))["error"]["code"],
        "INVALID_STATE"
    );
    assert_eq!(
        w.send(command(
            "stale",
            "resume_session",
            json!({"session_id":"00000000-0000-0000-0000-000000000000"})
        ))["error"]["code"],
        "STALE_SESSION"
    );
    assert_eq!(
        w.send(command(
            "resume",
            "resume_session",
            json!({"session_id":id})
        ))["ok"],
        true
    );
    assert_eq!(
        w.send(command(
            "source",
            "mock_segment",
            json!({"source":"after resume"})
        ))["ok"],
        true
    );
    let records = w.send(command("history", "get_history", json!({})))["result"]["records"]
        .as_array()
        .unwrap()
        .clone();
    assert_eq!(records.len(), 2);
    assert_eq!(records[0]["source"], "before pause");
    assert_eq!(records[1]["product_session_id"], id);
    assert!(
        records[1]["segment_id"].as_u64().unwrap() > records[0]["segment_id"].as_u64().unwrap()
    );
    assert!(records[1]["epoch"].as_u64().unwrap() > records[0]["epoch"].as_u64().unwrap());
    assert_eq!(
        w.send(command("stop", "stop_session", json!({"session_id":id})))["result"]["state"],
        "Stopping"
    );
    assert_eq!(
        w.send(command("state", "get_state", json!({})))["result"]["session"]["state"],
        "Idle"
    );
    let second = w.send(command("start", "start_session", config));
    assert_ne!(second["result"]["session_id"], id);
    assert_eq!(
        w.send(command("old", "stop_session", json!({"session_id":id})))["error"]["code"],
        "STALE_SESSION"
    );
    assert_eq!(
        w.send(command("history", "get_history", json!({})))["result"]["records"]
            .as_array()
            .unwrap()
            .len(),
        2
    );
    let mut active = second["result"]["session_id"].clone();
    for _ in 0..1001 {
        assert_eq!(
            w.send(command(
                "stop",
                "stop_session",
                json!({"session_id":active})
            ))["ok"],
            true
        );
        active = w.send(command(
            "start",
            "start_session",
            json!({"config":{"source_language":"en"},"history_policy":"retain"}),
        ))["result"]["session_id"]
            .clone();
    }
    let retained = w.send(command("retained", "get_history", json!({})));
    assert_eq!(retained["result"]["records"][0]["product_session_id"], id);
    w.send(command("exit", "shutdown", json!({})));
    w.finish();
}

#[test]
fn uuid_history_clear_is_scoped_and_preserves_resume_identity() {
    let mut w = Worker::start_args(vec!["--mock-pipeline", "--mock-session-control"]);
    hello(&mut w);
    let config = json!({"config":{"source_language":"en"},"history_policy":"retain"});
    let id =
        w.send(command("start", "start_session", config.clone()))["result"]["session_id"].clone();
    w.send(command("source", "mock_segment", json!({"source":"first"})));
    let old = w.send(command("before", "get_history", json!({})))["result"].clone();
    assert_eq!(
        w.send(command(
            "running",
            "clear_history",
            json!({"session_id":id})
        ))["error"]["code"],
        "INVALID_STATE"
    );
    w.send(command("pause", "pause_session", json!({"session_id":id})));
    assert_eq!(
        w.send(command("missing", "clear_history", json!({})))["error"]["code"],
        "INVALID_REQUEST"
    );
    assert_eq!(
        w.send(command(
            "stale",
            "clear_history",
            json!({"session_id":"00000000-0000-0000-0000-000000000000"})
        ))["error"]["code"],
        "STALE_SESSION"
    );
    let removed = w.send(command("clear", "clear_history", json!({"session_id":id})));
    assert_eq!(removed["result"]["removed_count"], 1);
    assert!(
        removed["result"]["history_version"].as_u64().unwrap()
            > old["history_version"].as_u64().unwrap()
    );
    assert_eq!(
        w.send(command("again", "clear_history", json!({"session_id":id})))["result"]
            ["removed_count"],
        0
    );
    assert_eq!(
        w.send(command(
            "snapshot",
            "get_history",
            json!({"expected_version":old["history_version"]})
        ))["error"]["code"],
        "STALE_SNAPSHOT"
    );
    assert!(
        w.send(command("empty", "get_history", json!({})))["result"]["records"]
            .as_array()
            .unwrap()
            .is_empty()
    );
    w.send(command(
        "resume",
        "resume_session",
        json!({"session_id":id}),
    ));
    w.send(command(
        "source2",
        "mock_segment",
        json!({"source":"after clear"}),
    ));
    let resumed =
        w.send(command("resumed", "get_history", json!({})))["result"]["records"][0].clone();
    assert_eq!(resumed["product_session_id"], id);
    assert_eq!(
        resumed["session_started_at_utc"],
        old["records"][0]["session_started_at_utc"]
    );
    assert!(
        resumed["segment_id"].as_u64().unwrap() > old["records"][0]["segment_id"].as_u64().unwrap()
    );
    w.send(command("stop", "stop_session", json!({"session_id":id})));
    let next = w.send(command("new", "start_session", config))["result"]["session_id"].clone();
    w.send(command(
        "source3",
        "mock_segment",
        json!({"source":"keep new"}),
    ));
    w.send(command("stop2", "stop_session", json!({"session_id":next})));
    assert_eq!(
        w.send(command(
            "clear-old",
            "clear_history",
            json!({"session_id":id})
        ))["result"]["removed_count"],
        1
    );
    let kept = w.send(command("kept", "get_history", json!({})))["result"]["records"].clone();
    assert_eq!(kept.as_array().unwrap().len(), 1);
    assert_eq!(kept[0]["product_session_id"], next);
    assert_eq!(
        w.send(command(
            "deleted-uuid",
            "clear_history",
            json!({"session_id":id})
        ))["error"]["code"],
        "STALE_SESSION"
    );
    assert!(w
        .events
        .iter()
        .any(|e| e["event"] == "history.changed" && e["payload"]["reason"] == "history_cleared"));
    w.send(command("exit", "shutdown", json!({})));
    w.finish();
}

#[test]
fn mock_source_translation_and_versioned_pages_round_trip() {
    let mut worker = Worker::start_with(true);
    hello(&mut worker);
    worker.send(command(
        "source",
        "mock_segment",
        json!({"source":"한국어 日本語 🙂\nnext"}),
    ));
    let page = worker.send(command("page", "get_history", json!({"limit":1})));
    let version = page["result"]["history_version"].as_u64().unwrap();
    let r = &page["result"]["records"][0];
    assert_eq!(r["source"], "한국어 日本語 🙂\nnext");
    assert_eq!(r["audio_end_s"], 0.032);
    assert_eq!(r["translation_state"], "Pending");
    let request = r["translation_request_id"].as_u64().unwrap();
    let wrong = worker.send(command(
        "wrong",
        "mock_translate",
        json!({"session_id":1,"epoch":1,"source_revision":1,"segment_id":1,"translation_request_id":request+1,"text":"bad"}),
    ));
    assert_eq!(wrong["error"]["code"], "STALE_RESULT");
    worker.send(command(
        "translate",
        "mock_translate",
        json!({"session_id":1,"epoch":1,"source_revision":1,"segment_id":1,"translation_request_id":request,"text":"번역\n🙂"}),
    ));
    let stale = worker.send(command(
        "stale",
        "get_history",
        json!({"expected_version":version}),
    ));
    assert_eq!(stale["error"]["code"], "STALE_SNAPSHOT");
    let fresh = worker.send(command("fresh", "get_history", json!({})));
    assert_eq!(fresh["result"]["records"][0]["translation"], "번역\n🙂");
    worker.send(command(
        "source2",
        "mock_segment",
        json!({"source":"second"}),
    ));
    let first = worker.send(command("first", "get_history", json!({"limit":1})));
    let second = worker.send(command(
        "second",
        "get_history",
        json!({"offset":1,"limit":1,"expected_version":first["result"]["history_version"]}),
    ));
    assert_eq!(second["result"]["records"][0]["segment_id"], 2);
    worker.send(command("stop", "shutdown", json!({})));
    let mut trailing = String::new();
    worker.output.read_to_string(&mut trailing).unwrap();
    for line in trailing.lines() {
        worker.events.push(serde_json::from_str(line).unwrap());
    }
    assert!(worker.events.iter().any(|e| e["event"] == "source.final"));
    assert!(worker
        .events
        .iter()
        .any(|e| e["event"] == "translation.updated"));
    let seqs: Vec<_> = worker
        .events
        .iter()
        .map(|e| e["seq"].as_u64().unwrap())
        .collect();
    assert!(seqs.windows(2).all(|w| w[0] < w[1]));
    worker.finish();
}

#[test]
fn opt_in_partial_revisions_freeze_at_final_and_pause_discards_active_source() {
    let mut w = Worker::start_args(vec!["--mock-pipeline", "--mock-session-control"]);
    hello(&mut w);
    let invalid = w.send(command(
        "invalid",
        "start_session",
        json!({"history_policy":"retain","config":{"source_language":"en","partial_enabled":"yes"}}),
    ));
    assert_eq!(invalid["error"]["code"], "INVALID_REQUEST");
    let start = w.send(command(
        "start",
        "start_session",
        json!({"history_policy":"retain","config":{"source_language":"en","partial_enabled":true}}),
    ));
    assert_eq!(start["ok"], true);
    let uuid = start["result"]["session_id"].clone();
    for text in ["first", "updated"] {
        assert_eq!(
            w.send(command(
                "partial",
                "mock_segment",
                json!({"kind":"partial","source":text})
            ))["ok"],
            true
        );
    }
    let partial =
        w.send(command("history", "get_history", json!({})))["result"]["records"][0].clone();
    assert_eq!(partial["source_state"], "Partial");
    assert_eq!(partial["source_revision"], 2);
    assert_eq!(partial["source"], "updated");
    assert_ne!(partial["translation_state"], "Pending");
    w.send(command(
        "final",
        "mock_segment",
        json!({"source":"confirmed"}),
    ));
    let final_record =
        w.send(command("history", "get_history", json!({})))["result"]["records"][0].clone();
    assert_eq!(final_record["segment_id"], partial["segment_id"]);
    assert_eq!(final_record["source_revision"], 3);
    assert_eq!(final_record["source_state"], "Final");
    w.send(command(
        "partial",
        "mock_segment",
        json!({"kind":"partial","source":"interrupted"}),
    ));
    w.send(command(
        "pause",
        "pause_session",
        json!({"session_id":uuid}),
    ));
    let records = w.send(command("history", "get_history", json!({})))["result"]["records"].clone();
    assert_eq!(records[1]["source_state"], "Discarded");
    w.send(command(
        "resume",
        "resume_session",
        json!({"session_id":uuid}),
    ));
    w.send(command(
        "final",
        "mock_segment",
        json!({"source":"resumed"}),
    ));
    let records = w.send(command("history", "get_history", json!({})))["result"]["records"].clone();
    assert_eq!(records.as_array().unwrap().len(), 3);
    assert!(records[2]["segment_id"].as_u64() > records[1]["segment_id"].as_u64());
    assert!(records[2]["epoch"].as_u64() > records[1]["epoch"].as_u64());
    w.send(command("stop", "stop_session", json!({"session_id":uuid})));
    w.send(command(
        "new",
        "start_session",
        json!({"history_policy":"retain","config":{"source_language":"en"}}),
    ));
    assert_eq!(
        w.send(command(
            "partial",
            "mock_segment",
            json!({"kind":"partial","source":"disabled"})
        ))["error"]["code"],
        "UNSUPPORTED_CAPABILITY"
    );
    w.send(command("shutdown", "shutdown", json!({})));
    w.finish();
}

#[test]
fn suppressed_mock_results_are_explicit_skips_and_real_repetition_is_retained() {
    let mut w = Worker::start_with(true);
    hello(&mut w);
    for outcome in ["no_speech", "overlap_only"] {
        assert_eq!(
            w.send(command(
                "source",
                "mock_segment",
                json!({"source":"fixture placeholder","outcome":outcome})
            ))["ok"],
            true
        );
    }
    for _ in 0..2 {
        w.send(command(
            "source",
            "mock_segment",
            json!({"source":"안 돼, 안 돼"}),
        ));
    }
    let records = w.send(command("history", "get_history", json!({})))["result"]["records"].clone();
    assert_eq!(records[0]["source_state"], "Skipped");
    assert_eq!(records[0]["source_reason"], "NoSpeech");
    assert_eq!(records[1]["source_reason"], "OverlapOnly");
    assert_ne!(records[0]["translation_state"], "Pending");
    assert_eq!(records[2]["source"], "안 돼, 안 돼");
    assert_eq!(records[3]["source"], "안 돼, 안 돼");
    w.send(command("stop", "shutdown", json!({})));
    w.finish();
}

#[test]
fn worst_case_escaped_text_history_stays_below_message_limit() {
    let mut worker = Worker::start_with(true);
    hello(&mut worker);
    for _ in 0..5 {
        worker.send(command(
            "source",
            "mock_segment",
            json!({"source":format!("a{}","\u{0001}".repeat(4095))}),
        ));
    }
    let page = worker.send(command("page", "get_history", json!({"limit":4})));
    assert_eq!(page["result"]["records"].as_array().unwrap().len(), 4);
    assert_eq!(page["result"]["next_offset"], 4);
    worker.send(command("stop", "shutdown", json!({})));
    worker.finish();
}

#[test]
fn unread_output_disconnects_worker_after_five_seconds() {
    let mut worker = Worker::start_with(true);
    hello(&mut worker);
    let input = worker.child.stdin.as_mut().unwrap();
    writeln!(
        input,
        "{}",
        command(
            "burst",
            "mock_burst",
            json!({"count":1100,"source":"x".repeat(4096)})
        )
    )
    .unwrap();
    input.flush().unwrap();
    let started = Instant::now();
    loop {
        if let Some(status) = worker.child.try_wait().unwrap() {
            assert!(!status.success());
            break;
        }
        assert!(
            started.elapsed() < Duration::from_secs(10),
            "stalled worker did not exit"
        );
        thread::sleep(Duration::from_millis(25));
    }
    assert!(started.elapsed() >= Duration::from_secs(4));
}

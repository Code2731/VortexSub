use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Child, ChildStdout, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use serde_json::{json, Value};

struct Worker {
    child: Child,
    output: BufReader<ChildStdout>,
}

impl Worker {
    fn start() -> Self {
        let mut child = Command::new(env!("CARGO_BIN_EXE_echosub-worker"))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .expect("start worker");
        let output = BufReader::new(child.stdout.take().expect("stdout pipe"));
        Self { child, output }
    }

    fn send(&mut self, request: Value) -> Value {
        let input = self.child.stdin.as_mut().expect("stdin pipe");
        writeln!(input, "{request}").expect("write command");
        input.flush().expect("flush command");
        self.response()
    }

    fn response(&mut self) -> Value {
        let mut line = String::new();
        self.output.read_line(&mut line).expect("read response");
        assert!(!line.is_empty(), "worker closed without response");
        let response: Value = serde_json::from_str(&line).expect("stdout must be NDJSON");
        assert_eq!(response["kind"], "response");
        assert_eq!(response["v"], 1);
        response
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
    assert_eq!(response["result"]["implementation"], "mock");
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

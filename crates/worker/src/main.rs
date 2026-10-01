use std::io::{self, BufRead, BufReader, Write};
use std::sync::{mpsc, Arc};
use std::time::Duration;
#[cfg(any(feature = "native-asr", test))]
mod asr_reconcile;
mod capture_runtime;
mod live_owner;
mod native_owner;
mod runtime;
#[cfg(any(feature = "native-asr", test))]
mod token_reconcile;
mod transport;

use serde_json::{json, Value};

const MAX_MESSAGE_BYTES: usize = 256 * 1024;
const PROTOCOL_VERSION: u64 = 1;

fn main() {
    if let Err(error) = serve() {
        eprintln!("worker I/O error: {error}");
        std::process::exit(1);
    }
}

fn serve() -> io::Result<()> {
    let outbox = Arc::new(transport::Outbox::default());
    let output = outbox.clone();
    let writer_thread = std::thread::spawn(move || output.write_to(io::stdout().lock()));
    let (send, receive) = mpsc::sync_channel(32);
    std::thread::spawn(move || {
        let mut reader = BufReader::new(io::stdin().lock());
        loop {
            let line = read_line(&mut reader);
            let done = !matches!(&line, Ok(Some(_)));
            if send.send(line).is_err() || done {
                break;
            }
        }
    });
    let mut writer = transport::ResponseWriter::new(&outbox);
    let args = std::env::args().collect::<Vec<_>>();
    let config = native_owner::config_from_args(&args).map_err(io::Error::other)?;
    let capture = args.iter().any(|a| a == "--diagnostic-capture");
    let live = args.iter().any(|a| a == "--live-asr");
    let sessions = args.iter().any(|a| a == "--session-control");
    let mock_sessions = args.iter().any(|a| a == "--mock-session-control");
    if (sessions && !live)
        || (mock_sessions
            && (sessions
                || capture
                || config.is_some()
                || !args.iter().any(|a| a == "--mock-pipeline")))
    {
        return Err(io::Error::other(
            "Session control requires live ASR; mock session control requires only mock pipeline",
        ));
    }
    if live && (!capture || !config.as_ref().is_some_and(|c| c.vad.is_some())) {
        return Err(io::Error::other(
            "Live ASR requires Windows capture and diagnostic ASR/VAD assets",
        ));
    }
    if capture
        && (!cfg!(windows)
            || (config.is_some() && !live)
            || args.iter().any(|a| a == "--mock-pipeline"))
    {
        return Err(io::Error::other(
            "Capture diagnostics require Windows and a separate mode",
        ));
    }
    let mut runtime =
        runtime::Runtime::new(args.iter().any(|a| a == "--mock-pipeline"), config, capture);
    runtime.session.enabled = sessions || mock_sessions;
    runtime.session.mock = mock_sessions;
    if args.iter().any(|a| a == "--diagnostic-translation") {
        runtime.enable_http_translation();
    }
    let mut hello_done = false;

    loop {
        runtime.poll(&outbox)?;
        if outbox.unhealthy() {
            runtime.finish();
            return Err(io::Error::other(
                "UI output stalled for 5 seconds or disconnected",
            ));
        }
        if !outbox.has_response_room() {
            outbox.wait_tick();
            continue;
        }
        let incoming = match receive.recv_timeout(Duration::from_millis(20)) {
            Ok(value) => value,
            Err(mpsc::RecvTimeoutError::Timeout) => continue,
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        };
        let line = match incoming {
            Ok(Some(line)) => line,
            Ok(None) => break,
            Err(LineError::TooLarge) => {
                write_response(
                    &mut writer,
                    Value::Null,
                    Err(("INVALID_REQUEST", "Message exceeds 256 KiB")),
                )?;
                break;
            }
            Err(LineError::Unterminated) => {
                write_response(
                    &mut writer,
                    Value::Null,
                    Err(("INVALID_REQUEST", "Message must end with newline")),
                )?;
                break;
            }
            Err(LineError::Io(error)) => return Err(error),
        };

        let request = match serde_json::from_slice::<Value>(&line) {
            Ok(request) => request,
            Err(_) => {
                write_response(
                    &mut writer,
                    Value::Null,
                    Err(("INVALID_REQUEST", "Invalid UTF-8 JSON")),
                )?;
                continue;
            }
        };

        let request_id = request
            .get("request_id")
            .and_then(Value::as_str)
            .filter(|id| !id.is_empty() && id.len() <= 128)
            .map(|id| Value::String(id.to_owned()))
            .unwrap_or(Value::Null);
        let Some(object) = request.as_object() else {
            write_response(
                &mut writer,
                request_id,
                Err(("INVALID_REQUEST", "Command must be a JSON object")),
            )?;
            continue;
        };
        if request_id.is_null()
            || object.get("kind").and_then(Value::as_str) != Some("command")
            || !object.get("params").is_some_and(Value::is_object)
        {
            write_response(
                &mut writer,
                request_id,
                Err(("INVALID_REQUEST", "Invalid command envelope")),
            )?;
            continue;
        }
        if object.get("v").and_then(Value::as_u64) != Some(PROTOCOL_VERSION) {
            write_response(
                &mut writer,
                request_id,
                Err(("PROTOCOL_MISMATCH", "Unsupported protocol major version")),
            )?;
            continue;
        }
        let Some(method) = object
            .get("method")
            .and_then(Value::as_str)
            .filter(|method| !method.is_empty())
        else {
            write_response(
                &mut writer,
                request_id,
                Err(("INVALID_REQUEST", "Missing method")),
            )?;
            continue;
        };
        let params = &object["params"];

        let result = match method {
            "hello" => {
                if params.get("protocol_major").and_then(Value::as_u64) != Some(PROTOCOL_VERSION) {
                    Err(("PROTOCOL_MISMATCH", "Unsupported protocol major version"))
                } else if params.get("client").and_then(Value::as_str).is_none() {
                    Err(("INVALID_REQUEST", "Missing client"))
                } else {
                    hello_done = true;
                    Ok(json!({
                        "protocol_major": PROTOCOL_VERSION,
                        "worker_version": env!("CARGO_PKG_VERSION"),
                        "platform": std::env::consts::OS,
                        "implementation": runtime.implementation(),
                        "capabilities": {
                            "system_audio": runtime.capture.enabled,
                            "output_device_selection": runtime.capture.enabled,
                            "capture_pcm": runtime.capture.enabled,
                            "live_asr": runtime.is_live(),
                            "source_partial": runtime.is_live() || runtime.session.mock,
                            "partial_translation": runtime.translator.enabled && (runtime.is_live() || runtime.session.mock),
                            "source_token_alignment": runtime.has_native(),
                            "session_control": runtime.session.enabled,
                            "session_history_uuid": runtime.session.enabled,
                            "history_export": runtime.session.enabled,
                            "history_clear": runtime.session.enabled,
                            "asr": runtime.has_native(),
                            "fixture_asr": runtime.has_native() && !runtime.is_live(),
                            "vad": runtime.has_vad(),
                            "translation": runtime.translator.enabled,
                            "events": true,
                            "history_snapshot": true,
                            "mock_pipeline": runtime.enabled
                        }
                    }))
                }
            }
            "ping" if hello_done => match params.get("nonce").and_then(Value::as_str) {
                Some(nonce) if nonce.len() <= 1024 => {
                    Ok(json!({"nonce": nonce, "worker_state": "Idle"}))
                }
                None => Err(("INVALID_REQUEST", "Missing nonce")),
                _ => Err(("INVALID_REQUEST", "Nonce exceeds 1024 bytes")),
            },
            "get_state" if hello_done => Ok(runtime.state(&outbox)),
            "get_history" if hello_done => runtime.history(params, &outbox),
            "export_history" if hello_done => runtime.export_history(params),
            "clear_history" if hello_done => runtime.clear_history(params, &outbox),
            "configure_translation" | "disable_translation" if hello_done => {
                runtime.translation_command(method, params, &outbox)
            }
            "start_session" | "pause_session" | "resume_session" | "stop_session" if hello_done => {
                runtime.session_command(method, params, &outbox)
            }
            "start_capture" | "stop_capture" if hello_done => {
                if runtime.session.enabled {
                    Err((
                        "UNSUPPORTED_CAPABILITY",
                        "Use UUID session commands in session control mode",
                    ))
                } else {
                    runtime.capture_command(method, params, &outbox)
                }
            }
            "mock_segment" | "mock_translate" | "mock_burst" if hello_done => {
                runtime.mock(method, params, &outbox)
            }
            "transcribe_fixture" | "reset_fixture_epoch" if hello_done => {
                runtime.fixture(method, params, &outbox)
            }
            "shutdown" => {
                write_response(&mut writer, request_id, Ok(json!({"accepted": true})))?;
                break;
            }
            "ping"
            | "get_state"
            | "get_history"
            | "export_history"
            | "clear_history"
            | "configure_translation"
            | "disable_translation"
            | "start_session"
            | "pause_session"
            | "resume_session"
            | "stop_session"
            | "mock_segment"
            | "mock_translate"
            | "mock_burst"
            | "start_capture"
            | "stop_capture"
            | "transcribe_fixture"
            | "reset_fixture_epoch" => Err(("INVALID_STATE", "Call hello first")),
            _ => Err(("UNSUPPORTED_CAPABILITY", "Method is not implemented")),
        };
        write_response(&mut writer, request_id, result)?;
    }
    runtime.interrupt();
    outbox.close();
    while !outbox.drained() {
        if outbox.unhealthy() {
            return Err(io::Error::other("UI output stalled during shutdown"));
        }
        outbox.wait_tick();
    }
    runtime.finish();
    writer_thread
        .join()
        .map_err(|_| io::Error::other("Writer panicked"))?
}

fn write_response<W: Write>(
    writer: &mut W,
    request_id: Value,
    result: Result<Value, (&str, &str)>,
) -> io::Result<()> {
    let response = match result {
        Ok(result) => json!({
            "v": PROTOCOL_VERSION,
            "kind": "response",
            "request_id": request_id,
            "ok": true,
            "result": result
        }),
        Err((code, message)) => json!({
            "v": PROTOCOL_VERSION,
            "kind": "response",
            "request_id": request_id,
            "ok": false,
            "error": {"code": code, "message": message}
        }),
    };
    serde_json::to_writer(&mut *writer, &response).map_err(io::Error::other)?;
    writer.write_all(b"\n")?;
    writer.flush()
}

enum LineError {
    TooLarge,
    Unterminated,
    Io(io::Error),
}

fn read_line<R: BufRead>(reader: &mut R) -> Result<Option<Vec<u8>>, LineError> {
    let mut line = Vec::new();
    loop {
        let available = reader.fill_buf().map_err(LineError::Io)?;
        if available.is_empty() {
            return if line.is_empty() {
                Ok(None)
            } else {
                Err(LineError::Unterminated)
            };
        }
        let end = available
            .iter()
            .position(|byte| *byte == b'\n')
            .map(|index| index + 1)
            .unwrap_or(available.len());
        let has_newline = available[end - 1] == b'\n';
        let payload_bytes = if has_newline { end - 1 } else { end };
        if line.len() + payload_bytes > MAX_MESSAGE_BYTES {
            return Err(LineError::TooLarge);
        }
        line.extend_from_slice(&available[..payload_bytes]);
        reader.consume(end);
        if has_newline {
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            return Ok(Some(line));
        }
    }
}

use std::io::{self, BufRead, BufReader, BufWriter, Write};

use serde_json::{json, Value};

const MAX_MESSAGE_BYTES: usize = 256 * 1024;
const PROTOCOL_VERSION: u64 = 1;

fn main() {
    if let Err(error) = serve(io::stdin().lock(), io::stdout().lock()) {
        eprintln!("worker I/O error: {error}");
        std::process::exit(1);
    }
}

fn serve<R: BufRead, W: Write>(input: R, output: W) -> io::Result<()> {
    let mut reader = BufReader::new(input);
    let mut writer = BufWriter::new(output);
    let mut hello_done = false;

    loop {
        let line = match read_line(&mut reader) {
            Ok(Some(line)) => line,
            Ok(None) => return Ok(()),
            Err(LineError::TooLarge) => {
                write_response(
                    &mut writer,
                    Value::Null,
                    Err(("INVALID_REQUEST", "Message exceeds 256 KiB")),
                )?;
                return Ok(());
            }
            Err(LineError::Unterminated) => {
                write_response(
                    &mut writer,
                    Value::Null,
                    Err(("INVALID_REQUEST", "Message must end with newline")),
                )?;
                return Ok(());
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
                        "implementation": "mock",
                        "capabilities": {
                            "system_audio": false,
                            "output_device_selection": false,
                            "asr": false,
                            "translation": false
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
            "get_state" if hello_done => Ok(json!({
                "session": {
                    "session_id": null,
                    "epoch": 0,
                    "state": "Idle",
                    "elapsed_ms": 0
                },
                "translator": {"state": "Unavailable"},
                "model": {"state": "NotInstalled"},
                "last_seq": 0
            })),
            "shutdown" => {
                write_response(&mut writer, request_id, Ok(json!({"accepted": true})))?;
                return Ok(());
            }
            "ping" | "get_state" => Err(("INVALID_STATE", "Call hello first")),
            _ => Err(("UNSUPPORTED_CAPABILITY", "Method is not implemented")),
        };
        write_response(&mut writer, request_id, result)?;
    }
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

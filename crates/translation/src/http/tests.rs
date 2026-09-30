use super::*;
use crate::{
    owner::{Output, Owner, SubmitError},
    prepare, Prepared,
};
use echosub_audio_core::{AudioIdentity, JobIdentity};
use echosub_pipeline_core::{TranslationJob, TranslationKey};
use std::{
    io::{Read, Write},
    net::TcpListener,
    thread,
};

fn job() -> TranslationJob {
    TranslationJob {
        key: TranslationKey {
            source: JobIdentity {
                audio: AudioIdentity {
                    session_id: 1,
                    epoch: 2,
                },
                segment_id: 3,
                source_revision: 4,
            },
            request_id: 5,
        },
        source: "Don't move: 42 enemies.".into(),
        context: vec!["Stay here.".into()],
        source_language: "en".into(),
        target_language: "ko".into(),
        deadline_ns: 8_000_000_000,
    }
}
fn request(budget: Duration) -> Request {
    let Prepared::Send(mut r) = prepare(&job(), "actual/model", 0).unwrap() else {
        panic!()
    };
    r.remaining = budget;
    r
}
fn response(status: u16, extra: &str, body: &str) -> String {
    format!(
        "HTTP/1.1 {status} Fixture\r\nContent-Length: {}\r\nConnection: close\r\n{extra}\r\n{body}",
        body.len()
    )
}
fn success() -> String {
    response(
        200,
        "",
        r#"{"choices":[{"finish_reason":"stop","message":{"content":"움직이지 마: 적 42명."}}]}"#,
    )
}
fn server(responses: Vec<(Duration, String)>) -> (Endpoint, thread::JoinHandle<Vec<String>>) {
    let (endpoint, thread, _) = server_observed(responses);
    (endpoint, thread)
}
fn server_observed(
    responses: Vec<(Duration, String)>,
) -> (
    Endpoint,
    thread::JoinHandle<Vec<String>>,
    std::sync::mpsc::Receiver<()>,
) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let endpoint = Endpoint::parse(&format!("http://{}", listener.local_addr().unwrap())).unwrap();
    let (notify, observed) = std::sync::mpsc::channel();
    let thread = thread::spawn(move || {
        let mut requests = Vec::new();
        for (delay, response) in responses {
            let start = Instant::now();
            let mut socket = loop {
                match listener.accept() {
                    Ok((socket, _)) => break socket,
                    Err(e)
                        if e.kind() == std::io::ErrorKind::WouldBlock
                            && start.elapsed() < Duration::from_secs(3) =>
                    {
                        thread::sleep(Duration::from_millis(1))
                    }
                    Err(e) => panic!("fixture accept: {e}"),
                }
            };
            socket.set_nonblocking(false).unwrap();
            socket
                .set_read_timeout(Some(Duration::from_secs(2)))
                .unwrap();
            let mut bytes = Vec::new();
            let mut buffer = [0; 1024];
            loop {
                let n = socket.read(&mut buffer).unwrap();
                assert!(n > 0 && bytes.len() + n < 64 * 1024);
                bytes.extend_from_slice(&buffer[..n]);
                if let Some(header_end) = bytes.windows(4).position(|b| b == b"\r\n\r\n") {
                    let headers = String::from_utf8_lossy(&bytes[..header_end]);
                    let length = headers
                        .lines()
                        .find_map(|line| {
                            line.to_ascii_lowercase()
                                .strip_prefix("content-length:")
                                .map(|s| s.trim().parse::<usize>().unwrap())
                        })
                        .unwrap_or(0);
                    if bytes.len() >= header_end + 4 + length {
                        break;
                    }
                }
            }
            requests.push(String::from_utf8(bytes).unwrap());
            let _ = notify.send(());
            thread::sleep(delay);
            let _ = socket.write_all(response.as_bytes());
        }
        requests
    });
    (endpoint, thread, observed)
}
fn client(endpoint: Endpoint) -> HttpClient {
    HttpClient::new(endpoint, None).unwrap()
}

#[tokio::test(flavor = "current_thread")]
async fn real_http_catalog_selection_and_escaped_translation() {
    let (endpoint, thread) = server(vec![
        (
            Duration::ZERO,
            response(200, "", r#"{"data":[{"id":"actual/model"}]}"#),
        ),
        (Duration::ZERO, success()),
    ]);
    let c = HttpClient::new(endpoint, Some("fixture-secret")).unwrap();
    let cancel = Cancellation::default();
    let ids = c.models(Duration::from_secs(2), &cancel).await.unwrap();
    assert_eq!(select_model(&ids, None).unwrap(), "actual/model");
    assert!(select_model(&ids, Some("invented")).is_err());
    assert!(select_model(&["one".into(), "two".into()], None).is_err());
    assert_eq!(
        c.translate(&request(Duration::from_secs(2)), &cancel)
            .await
            .unwrap(),
        "움직이지 마: 적 42명."
    );
    let requests = thread.join().unwrap();
    assert!(requests[0].starts_with("GET /v1/models "));
    assert!(requests[1].starts_with("POST /v1/chat/completions "));
    assert!(requests[1].contains("Bearer fixture-secret"));
    let body: serde_json::Value =
        serde_json::from_str(requests[1].split_once("\r\n\r\n").unwrap().1).unwrap();
    assert_eq!(body["model"], "actual/model");
    assert!(body.get("tools").is_none());
}
#[tokio::test(flavor = "current_thread")]
async fn redirect_is_returned_without_contacting_target() {
    let target = TcpListener::bind("127.0.0.1:0").unwrap();
    target.set_nonblocking(true).unwrap();
    let (endpoint, thread) = server(vec![(
        Duration::ZERO,
        response(
            302,
            &format!("Location: http://{}/leak\r\n", target.local_addr().unwrap()),
            "",
        ),
    )]);
    assert_eq!(
        client(endpoint)
            .models(Duration::from_secs(1), &Cancellation::default())
            .await,
        Err(Failure::Status(302))
    );
    assert_eq!(thread.join().unwrap().len(), 1);
    assert!(matches!(target.accept(), Err(e) if e.kind() == std::io::ErrorKind::WouldBlock));
}
#[tokio::test(flavor = "current_thread")]
async fn retry_5xx_once_and_do_not_retry_auth_or_invalid_json() {
    let (endpoint, thread) = server(vec![
        (Duration::ZERO, response(500, "", "")),
        (Duration::ZERO, success()),
    ]);
    assert!(client(endpoint)
        .translate(&request(Duration::from_secs(2)), &Cancellation::default())
        .await
        .is_ok());
    assert_eq!(thread.join().unwrap().len(), 2);
    for (status, body, expected) in [
        (401, "", Failure::Status(401)),
        (404, "", Failure::Status(404)),
        (200, "bad", Failure::Contract(Error::InvalidResponse)),
    ] {
        let (endpoint, thread) = server(vec![(Duration::ZERO, response(status, "", body))]);
        assert_eq!(
            client(endpoint)
                .translate(&request(Duration::from_secs(1)), &Cancellation::default())
                .await,
            Err(expected)
        );
        assert_eq!(thread.join().unwrap().len(), 1);
    }
}
#[tokio::test(flavor = "current_thread")]
async fn retry_after_stays_inside_original_budget() {
    let (endpoint, thread) = server(vec![
        (Duration::ZERO, response(429, "Retry-After: 0\r\n", "")),
        (Duration::ZERO, success()),
    ]);
    assert!(client(endpoint)
        .translate(&request(Duration::from_secs(2)), &Cancellation::default())
        .await
        .is_ok());
    assert_eq!(thread.join().unwrap().len(), 2);
    let (endpoint, thread) = server(vec![(
        Duration::ZERO,
        response(429, "Retry-After: 10\r\n", ""),
    )]);
    assert_eq!(
        client(endpoint)
            .translate(
                &request(Duration::from_millis(300)),
                &Cancellation::default()
            )
            .await,
        Err(Failure::Status(429))
    );
    thread.join().unwrap();
}
#[tokio::test(flavor = "current_thread")]
async fn deadline_covers_headers_body_and_retry_without_reset() {
    let (endpoint, thread) = server(vec![(Duration::from_millis(250), success())]);
    let timer = Instant::now();
    assert_eq!(
        client(endpoint)
            .translate(
                &request(Duration::from_millis(70)),
                &Cancellation::default()
            )
            .await,
        Err(Failure::Deadline)
    );
    assert!(timer.elapsed() < Duration::from_secs(1));
    thread.join().unwrap();
    let (endpoint, thread) = server(vec![
        (Duration::ZERO, response(500, "", "")),
        (Duration::from_millis(300), success()),
    ]);
    assert_eq!(
        client(endpoint)
            .translate(
                &request(Duration::from_millis(250)),
                &Cancellation::default()
            )
            .await,
        Err(Failure::Deadline)
    );
    thread.join().unwrap();
}
#[tokio::test(flavor = "current_thread")]
async fn cancellation_interrupts_inflight_and_pre_cancel_sends_nothing() {
    let (endpoint, thread) = server(vec![(Duration::from_millis(250), success())]);
    let cancel = Cancellation::default();
    let trigger = cancel.clone();
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(50)).await;
        trigger.cancel();
    });
    assert_eq!(
        client(endpoint)
            .translate(&request(Duration::from_secs(2)), &cancel)
            .await,
        Err(Failure::Cancelled)
    );
    thread.join().unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let endpoint = Endpoint::parse(&format!("http://{}", listener.local_addr().unwrap())).unwrap();
    assert_eq!(
        client(endpoint)
            .models(Duration::from_secs(1), &cancel)
            .await,
        Err(Failure::Cancelled)
    );
    assert!(listener.accept().is_err());
}
#[tokio::test(flavor = "current_thread")]
async fn oversized_fixed_and_chunked_bodies_are_bounded() {
    let large = "a".repeat(MAX_RESPONSE_BYTES + 1);
    for reply in [response(200,"",&large), format!("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n{:x}\r\n{large}\r\n0\r\n\r\n", large.len())] {
        let (endpoint, thread) = server(vec![(Duration::ZERO, reply)]);
        assert_eq!(client(endpoint).models(Duration::from_secs(2), &Cancellation::default()).await, Err(Failure::Contract(Error::OversizedResponse)));
        thread.join().unwrap();
    }
}
#[test]
fn owner_retains_single_flight_until_completion_poll_and_preserves_key() {
    let (endpoint, thread, observed) =
        server_observed(vec![(Duration::from_millis(100), success())]);
    let mut owner = Owner::new(endpoint, None).unwrap();
    let j = job();
    owner.translate(&j, "actual/model", 0).unwrap();
    assert_eq!(owner.models(Duration::from_secs(1)), Err(SubmitError::Busy));
    observed.recv_timeout(Duration::from_secs(2)).unwrap();
    owner.cancel();
    assert_eq!(owner.models(Duration::from_secs(1)), Err(SubmitError::Busy));
    let start = Instant::now();
    let complete = loop {
        if let Some(c) = owner.poll() {
            break c;
        }
        assert!(start.elapsed() < Duration::from_secs(2));
        thread::sleep(Duration::from_millis(1));
    };
    assert_eq!(complete.key, Some(j.key));
    assert!(matches!(complete.result, Err(Failure::Cancelled)));
    let mut bypass = j;
    bypass.target_language = "en".into();
    owner.translate(&bypass, "", 0).unwrap();
    let result = loop {
        if let Some(c) = owner.poll() {
            break c;
        }
        thread::sleep(Duration::from_millis(1));
    };
    assert!(matches!(result.result, Ok(Output::Bypass)));
    drop(owner);
    thread.join().unwrap();
}
#[test]
fn authentication_rejects_control_characters_without_echoing_token() {
    for token in ["", "abc\r\nSecret: leak", "with space", "한글"] {
        assert!(matches!(
            HttpClient::new(Endpoint::parse("http://127.0.0.1:1").unwrap(), Some(token)),
            Err(Failure::InvalidToken)
        ));
    }
}

#[tokio::test(flavor = "current_thread")]
async fn body_stall_expires_even_after_success_headers() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let endpoint = Endpoint::parse(&format!("http://{}", listener.local_addr().unwrap())).unwrap();
    let thread = thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        socket
            .set_read_timeout(Some(Duration::from_secs(2)))
            .unwrap();
        let mut buffer = [0; 4096];
        socket.read(&mut buffer).unwrap();
        socket
            .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n")
            .unwrap();
        thread::sleep(Duration::from_millis(200));
    });
    assert_eq!(
        client(endpoint)
            .models(Duration::from_millis(60), &Cancellation::default())
            .await,
        Err(Failure::Deadline)
    );
    thread.join().unwrap();
}

#[tokio::test(flavor = "current_thread")]
async fn second_server_error_is_terminal_and_zero_budget_sends_nothing() {
    let (endpoint, thread) = server(vec![
        (Duration::ZERO, response(503, "", "")),
        (Duration::ZERO, response(503, "", "")),
    ]);
    assert_eq!(
        client(endpoint)
            .models(Duration::from_secs(1), &Cancellation::default())
            .await,
        Err(Failure::Status(503))
    );
    assert_eq!(thread.join().unwrap().len(), 2);
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let endpoint = Endpoint::parse(&format!("http://{}", listener.local_addr().unwrap())).unwrap();
    assert_eq!(
        client(endpoint)
            .models(Duration::ZERO, &Cancellation::default())
            .await,
        Err(Failure::Deadline)
    );
    assert!(listener.accept().is_err());
}

#[test]
fn dropping_active_owner_cancels_and_joins() {
    let (endpoint, thread, observed) =
        server_observed(vec![(Duration::from_millis(150), success())]);
    let mut owner = Owner::new(endpoint, None).unwrap();
    owner.translate(&job(), "actual/model", 0).unwrap();
    observed.recv_timeout(Duration::from_secs(2)).unwrap();
    let start = Instant::now();
    drop(owner);
    assert!(start.elapsed() < Duration::from_secs(1));
    thread.join().unwrap();
}

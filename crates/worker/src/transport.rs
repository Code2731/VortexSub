use serde_json::{json, Value};
use std::collections::VecDeque;
use std::io::{self, Write};
use std::sync::{Condvar, Mutex};
use std::time::{Duration, Instant};

pub const MAX_BYTES: usize = 256 * 1024;
const EVENTS: usize = 256;
const RESPONSES: usize = 32;
struct Event {
    bytes: Vec<u8>,
    coalesce: Option<String>,
}
#[derive(Default)]
struct State {
    responses: VecDeque<Vec<u8>>,
    events: VecDeque<Event>,
    seq: u64,
    closing: bool,
    writing_since: Option<Instant>,
    failed: bool,
}
#[derive(Default)]
pub struct Outbox {
    state: Mutex<State>,
    wake: Condvar,
}
fn encoded(value: Value) -> io::Result<Vec<u8>> {
    let mut bytes = serde_json::to_vec(&value).map_err(io::Error::other)?;
    if bytes.len() > MAX_BYTES {
        return Err(io::Error::other("Outbound message exceeds 256 KiB"));
    }
    bytes.push(b'\n');
    Ok(bytes)
}
impl Outbox {
    #[cfg(all(test, feature = "native-asr"))]
    pub(crate) fn drain_probe_events(&self) -> Vec<Value> {
        self.state
            .lock()
            .unwrap()
            .events
            .drain(..)
            .map(|event| serde_json::from_slice(&event.bytes).unwrap())
            .collect()
    }
    pub fn last_seq(&self) -> u64 {
        self.state.lock().unwrap().seq
    }
    pub fn has_response_room(&self) -> bool {
        self.state.lock().unwrap().responses.len() < RESPONSES
    }
    pub fn response(&self, bytes: Vec<u8>) -> io::Result<()> {
        if bytes.len() > MAX_BYTES + 1 || !bytes.ends_with(b"\n") {
            return Err(io::Error::other("Invalid outbound response line"));
        }
        let mut s = self.state.lock().unwrap();
        if s.responses.len() == RESPONSES {
            return Err(io::Error::other("Response reservation exhausted"));
        }
        s.responses.push_back(bytes);
        self.wake.notify_all();
        Ok(())
    }
    pub fn publish(&self, name: &str, payload: Value, coalesce: Option<String>) -> io::Result<()> {
        let mut s = self.state.lock().unwrap();
        let next = s
            .seq
            .checked_add(1)
            .ok_or_else(|| io::Error::other("Event sequence exhausted"))?;
        let bytes =
            encoded(json!({"v":1,"kind":"event","seq":next,"event":name,"payload":payload}))?;
        s.seq = next;
        if let Some(ref key) = coalesce {
            s.events.retain(|e| e.coalesce.as_ref() != Some(key));
        }
        if s.events.len() == EVENTS {
            // Store is authoritative. Replace an undeliverable backlog with recovery intent.
            s.events.clear();
            s.seq = s
                .seq
                .checked_add(1)
                .ok_or_else(|| io::Error::other("Event sequence exhausted"))?;
            let marker = encoded(
                json!({"v":1,"kind":"event","seq":s.seq,"event":"snapshot.required","payload":{"reason":"event_overflow"}}),
            )?;
            s.events.push_back(Event {
                bytes: marker,
                coalesce: None,
            });
        } else {
            s.events.push_back(Event { bytes, coalesce });
        }
        self.wake.notify_all();
        Ok(())
    }
    fn take(&self) -> Option<Vec<u8>> {
        let mut s = self.state.lock().unwrap();
        loop {
            let next = s
                .responses
                .pop_front()
                .or_else(|| s.events.pop_front().map(|e| e.bytes));
            if next.is_some() {
                s.writing_since = Some(Instant::now());
                self.wake.notify_all();
                return next;
            }
            if s.closing {
                return None;
            }
            s = self.wake.wait(s).unwrap();
        }
    }
    pub fn write_to<W: Write>(&self, mut writer: W) -> io::Result<()> {
        while let Some(bytes) = self.take() {
            let result = (|| {
                let mut offset = 0;
                while offset < bytes.len() {
                    match writer.write(&bytes[offset..]) {
                        Ok(0) => {
                            return Err(io::Error::new(
                                io::ErrorKind::WriteZero,
                                "Writer made no progress",
                            ))
                        }
                        Ok(n) => {
                            offset += n;
                            self.state.lock().unwrap().writing_since = Some(Instant::now());
                        }
                        Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
                        Err(e) => return Err(e),
                    }
                }
                writer.flush()
            })();
            let mut s = self.state.lock().unwrap();
            s.writing_since = None;
            s.failed = result.is_err();
            self.wake.notify_all();
            result?;
        }
        Ok(())
    }
    pub fn unhealthy(&self) -> bool {
        let s = self.state.lock().unwrap();
        s.failed
            || s.writing_since
                .is_some_and(|t| t.elapsed() >= Duration::from_secs(5))
    }
    pub fn close(&self) {
        self.state.lock().unwrap().closing = true;
        self.wake.notify_all();
    }
    pub fn drained(&self) -> bool {
        let s = self.state.lock().unwrap();
        s.responses.is_empty() && s.events.is_empty() && s.writing_since.is_none()
    }
    pub fn wait_tick(&self) {
        let guard = self.state.lock().unwrap();
        drop(
            self.wake
                .wait_timeout(guard, Duration::from_millis(20))
                .unwrap(),
        );
    }
}
/// Existing response serializer writes into a bounded line, then reserves a slot.
pub struct ResponseWriter<'a> {
    pub outbox: &'a Outbox,
    bytes: Vec<u8>,
}
impl<'a> ResponseWriter<'a> {
    pub fn new(outbox: &'a Outbox) -> Self {
        Self {
            outbox,
            bytes: Vec::new(),
        }
    }
}
impl Write for ResponseWriter<'_> {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        if self.bytes.len() + buf.len() > MAX_BYTES + 1 {
            return Err(io::Error::other("Outbound message exceeds 256 KiB"));
        }
        self.bytes.extend_from_slice(buf);
        Ok(buf.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        self.outbox.response(std::mem::take(&mut self.bytes))
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn overflow_is_bounded_and_requests_recovery() {
        let q = Outbox::default();
        for _ in 0..257 {
            q.publish("source.final", json!({}), None).unwrap();
        }
        let s = q.state.lock().unwrap();
        assert_eq!(s.events.len(), 1);
        assert_eq!(s.seq, 258);
        let v: Value = serde_json::from_slice(&s.events[0].bytes).unwrap();
        assert_eq!(v["event"], "snapshot.required");
    }
    #[test]
    fn coalescing_keeps_sequence_order_and_exposes_loss() {
        let q = Outbox::default();
        q.publish("metrics", json!({"value":1}), Some("metrics".into()))
            .unwrap();
        q.publish("source.final", json!({}), None).unwrap();
        q.publish("metrics", json!({"value":2}), Some("metrics".into()))
            .unwrap();
        let s = q.state.lock().unwrap();
        let seqs: Vec<_> = s
            .events
            .iter()
            .map(|e| {
                serde_json::from_slice::<Value>(&e.bytes).unwrap()["seq"]
                    .as_u64()
                    .unwrap()
            })
            .collect();
        assert_eq!(seqs, vec![2, 3]);
    }
    #[test]
    fn responses_have_reserved_capacity_and_priority() {
        let q = Outbox::default();
        for _ in 0..256 {
            q.publish("source.final", json!({}), None).unwrap();
        }
        for _ in 0..32 {
            q.response(b"response\n".to_vec()).unwrap();
        }
        assert!(!q.has_response_room());
        assert!(q.response(vec![]).is_err());
        assert_eq!(q.take().unwrap(), b"response\n");
        assert!(q.has_response_room());
    }
    #[test]
    fn oversize_event_does_not_advance_sequence() {
        let q = Outbox::default();
        assert!(q
            .publish("metrics", json!({"text":"x".repeat(MAX_BYTES)}), None)
            .is_err());
        assert_eq!(q.last_seq(), 0);
    }
    #[test]
    fn stalled_writer_and_write_errors_are_visible() {
        let q = Outbox::default();
        q.state.lock().unwrap().writing_since = Some(Instant::now() - Duration::from_secs(5));
        assert!(q.unhealthy());
        q.state.lock().unwrap().writing_since = None;
        q.response(b"response\n".to_vec()).unwrap();
        struct Broken;
        impl Write for Broken {
            fn write(&mut self, _: &[u8]) -> io::Result<usize> {
                Err(io::Error::other("broken"))
            }
            fn flush(&mut self) -> io::Result<()> {
                Ok(())
            }
        }
        assert!(q.write_to(Broken).is_err());
        assert!(q.unhealthy());
    }
}

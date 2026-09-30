//! UUID control adapter. Numeric pipeline/history identities remain explicit.
use super::{Reply, Runtime};
use crate::transport::Outbox;
use echosub_audio_core::AudioIdentity;
use serde_json::{json, Value};
use std::collections::{BTreeMap, BTreeSet};

pub struct Session {
    pub enabled: bool,
    pub mock: bool,
    pub state: &'static str,
    id: Option<String>,
    internal_id: u64,
    started_ns: u64,
    ended_ns: Option<u64>,
    config: Value,
    metadata: BTreeMap<u64, (String, u64, String)>,
}
impl Default for Session {
    fn default() -> Self {
        Self {
            enabled: false,
            mock: false,
            state: "Idle",
            id: None,
            internal_id: 1,
            started_ns: 0,
            ended_ns: None,
            config: Value::Null,
            metadata: BTreeMap::new(),
        }
    }
}
impl Session {
    pub fn export_info(&self, uuid: &str) -> Option<(u64, u64, &str)> {
        self.metadata.iter().find_map(|(key, (id, origin, utc))| {
            (id == uuid).then_some((*key, *origin, utc.as_str()))
        })
    }
    pub fn cleared_history(&mut self, internal_id: u64) {
        // A paused current session can resume with the same UTC and audio origin.
        if internal_id != self.internal_id {
            self.metadata.remove(&internal_id);
        }
    }
    pub fn decorate_record(&self, internal_id: u64, start: u64, end: u64, value: &mut Value) {
        if let Some((uuid, origin, utc)) = self.metadata.get(&internal_id) {
            value["product_session_id"] = json!(uuid);
            value["session_started_at_utc"] = json!(utc);
            value["session_audio_start_s"] = json!(start.saturating_sub(*origin) as f64 / 16000.);
            value["session_audio_end_s"] = json!(end.saturating_sub(*origin) as f64 / 16000.);
        }
    }
    pub fn value(&self, now: u64, identity: AudioIdentity) -> Value {
        let elapsed = if self.id.is_some() {
            self.ended_ns.unwrap_or(now).saturating_sub(self.started_ns)
        } else {
            0
        };
        json!({"session_id":self.id,"started_at_utc":self.metadata.get(&self.internal_id).map(|(_,_,utc)|utc),"internal_session_id":if self.id.is_some(){Some(self.internal_id)}else{None},
            "epoch":if self.id.is_some(){identity.epoch}else{0},"state":self.state,
            "elapsed_ms":elapsed/1_000_000,"elapsed_s":elapsed as f64/1e9,
            "audio_origin_s":self.started_ns as f64/1e9,"history_policy":"retain",
            "implementation":if self.mock{"mock-session-control"}else{"uuid-control-adapter"}})
    }
}

pub(super) fn new_uuid() -> Result<String, (&'static str, &'static str)> {
    #[cfg(windows)]
    {
        // CoCreateGuid does not require COM initialization or a capture owner.
        let guid = unsafe { windows::Win32::System::Com::CoCreateGuid() }
            .map_err(|_| ("INTERNAL_ERROR", "Session UUID unavailable"))?;
        Ok(format!("{guid:?}").to_ascii_lowercase())
    }
    #[cfg(not(windows))]
    {
        use std::io::Read;
        let mut bytes = [0u8; 16];
        std::fs::File::open("/dev/urandom")
            .and_then(|mut f| f.read_exact(&mut bytes))
            .map_err(|_| ("INTERNAL_ERROR", "Session UUID unavailable"))?;
        bytes[6] = (bytes[6] & 15) | 64;
        bytes[8] = (bytes[8] & 63) | 128;
        let s = bytes.iter().map(|b| format!("{b:02x}")).collect::<String>();
        Ok(format!(
            "{}-{}-{}-{}-{}",
            &s[..8],
            &s[8..12],
            &s[12..16],
            &s[16..20],
            &s[20..]
        ))
    }
}

impl Runtime {
    fn session_transition(&mut self, state: &'static str, q: &Outbox) -> Reply {
        self.session.state = state;
        q.publish(
            "session.state",
            self.session.value(self.now(), self.epoch),
            None,
        )
        .map_err(|_| ("INTERNAL_ERROR", "Session event unavailable"))?;
        Ok(
            json!({"accepted":true,"session_id":self.session.id,"epoch":self.epoch.epoch,"state":state}),
        )
    }
    pub fn session_command(&mut self, method: &str, p: &Value, q: &Outbox) -> Reply {
        if !self.session.enabled {
            return Err(("UNSUPPORTED_CAPABILITY", "Session control requires opt-in"));
        }
        if method == "start_session" {
            if self.session.state != "Idle"
                || (!self.session.mock
                    && (self.model_state != "Ready"
                        || !self.capture.startable()
                        || self.live_owner.is_some()))
            {
                return Err(("INVALID_STATE", "Session or owners are not ready"));
            }
            if p.get("history_policy").and_then(Value::as_str) != Some("retain") {
                return Err(("INVALID_REQUEST", "Explicit retain history policy required"));
            }
            let config = p
                .get("config")
                .and_then(Value::as_object)
                .ok_or(("INVALID_REQUEST", "Session config required"))?;
            if config
                .keys()
                .any(|k| k != "source_language" && k != "device_id" && k != "partial_enabled")
            {
                return Err(("INVALID_REQUEST", "Unsupported session config field"));
            }
            let language = config
                .get("source_language")
                .and_then(Value::as_str)
                .filter(|s| matches!(*s, "en" | "ja" | "ko"))
                .ok_or(("INVALID_REQUEST", "Source language must be en, ja or ko"))?;
            let device = config.get("device_id").cloned().unwrap_or(Value::Null);
            let partial_enabled = match config.get("partial_enabled") {
                None => false,
                Some(v) => v
                    .as_bool()
                    .ok_or(("INVALID_REQUEST", "partial_enabled must be boolean"))?,
            };
            if !device.is_null()
                && !device
                    .as_str()
                    .is_some_and(|s| !s.is_empty() && s.len() <= 2048 && !s.contains('\0'))
            {
                return Err(("INVALID_REQUEST", "Invalid render endpoint ID"));
            }
            let id = new_uuid()?;
            let utc = super::export::utc_now()?;
            let internal_id = self
                .epoch
                .session_id
                .checked_add(1)
                .ok_or(("INTERNAL_ERROR", "Session identity exhausted"))?;
            // At most 1,000 retained sessions plus the current session. Empty sessions
            // cannot evict metadata for records still present in bounded history.
            let retained = self
                .core
                .history_identities()
                .map(|i| i.session_id)
                .collect::<BTreeSet<_>>();
            self.session
                .metadata
                .retain(|key, _| retained.contains(key));
            let started_ns = self.now();
            let audio_origin = echosub_audio_core::session_sample_from_ns(started_ns)
                .map_err(|_| ("INTERNAL_ERROR", "Session clock exhausted"))?;
            self.session
                .metadata
                .insert(internal_id, (id.clone(), audio_origin, utc));
            self.session.id = Some(id);
            self.session.internal_id = internal_id;
            self.session.started_ns = started_ns;
            self.session.ended_ns = None;
            self.partial_enabled = partial_enabled;
            self.session.config =
                json!({"language":language,"device_id":device,"partial_enabled":partial_enabled});
            self.epoch = AudioIdentity {
                session_id: internal_id,
                epoch: 0,
            };
            // A new session owns a new ring; outstanding immutable PCM leases remain
            // in the existing bounded pool until native completion acknowledges them.
            self.ring = echosub_audio_core::RollingAudio::new(
                echosub_audio_core::MAX_ROLLING_SAMPLES,
                self.epoch,
                0,
            )
            .map_err(|_| ("INTERNAL_ERROR", "Session ring unavailable"))?;
            self.next_segment = 1;
            if !self.session.mock {
                self.capture.set_previous_end(0);
            }
            self.session_start(q)?;
            return self.session_transition("Preparing", q);
        }
        if self.session.id.is_none()
            || p.get("session_id").and_then(Value::as_str) != self.session.id.as_deref()
        {
            return Err(("STALE_SESSION", "Session identity does not match"));
        }
        match method {
            "pause_session" if matches!(self.session.state, "Preparing" | "Running") => {
                self.session_stop_input(q)?;
                self.session_transition("Paused", q)
            }
            "resume_session" if self.session.state == "Paused" => {
                if !self.session.mock
                    && (!self.capture.startable()
                        || self.live_owner.is_some()
                        || self.model_state != "Ready")
                {
                    return Err(("INVALID_STATE", "Capture and VAD must finish stopping"));
                }
                self.session_start(q)?;
                self.session_transition("Preparing", q)
            }
            "stop_session" if self.session.state != "Idle" => {
                if self.session.state != "Stopping" {
                    self.session_stop_input(q)?;
                }
                self.session_transition("Stopping", q)
            }
            _ => Err(("INVALID_STATE", "Session command is invalid in this state")),
        }
    }
    fn session_start(&mut self, q: &Outbox) -> Reply {
        if self.session.mock {
            self.live_segment = None;
            self.epoch.epoch = self
                .epoch
                .epoch
                .checked_add(1)
                .ok_or(("INTERNAL_ERROR", "Epoch exhausted"))?;
            self.core
                .restart(self.epoch, false, self.now())
                .map_err(super::core_error)?;
            self.ring
                .reset(
                    self.epoch,
                    echosub_audio_core::session_sample_from_ns(self.now())
                        .map_err(|_| ("INTERNAL_ERROR", "Capture clock exhausted"))?
                        .max(self.ring.retained_range().end),
                )
                .map_err(|_| ("INTERNAL_ERROR", "Ring reset failed"))?;
            q.publish(
                "history.changed",
                json!({"history_version":self.core.version()}),
                None,
            )
            .map_err(|_| ("INTERNAL_ERROR", "History event unavailable"))?;
            Ok(json!({}))
        } else {
            self.capture_command("start_capture", &self.session.config.clone(), q)
        }
    }
    fn session_stop_input(&mut self, q: &Outbox) -> Reply {
        if self.session.mock {
            self.live_segment = None;
            self.epoch.epoch = self
                .epoch
                .epoch
                .checked_add(1)
                .ok_or(("INTERNAL_ERROR", "Epoch exhausted"))?;
            self.core
                .restart(self.epoch, false, self.now())
                .map_err(super::core_error)?;
            // Mock translation has no asynchronous owner; acknowledge cancellation here.
            if let Some(job) = self.translation.take() {
                self.core
                    .complete_translation(
                        job.key,
                        echosub_pipeline_core::Outcome::Cancelled,
                        self.now(),
                    )
                    .map_err(super::core_error)?;
            }
            q.publish(
                "history.changed",
                json!({"history_version":self.core.version()}),
                None,
            )
            .map_err(|_| ("INTERNAL_ERROR", "History event unavailable"))?;
            Ok(json!({}))
        } else {
            self.capture_command("stop_capture", &json!({}), q)
        }
    }
    pub fn poll_session(&mut self, q: &Outbox) -> std::io::Result<()> {
        if !self.session.enabled {
            return Ok(());
        }
        let capture = self.capture.value();
        let next = if self.session.state == "Stopping" {
            (self.capture.startable() && self.live_owner.is_none() && self.flight.is_none())
                .then_some("Idle")
        } else if matches!(self.session.state, "Preparing" | "Running")
            && !self.session.mock
            && self.capture.failed()
        {
            Some("Error")
        } else if self.session.state == "Preparing"
            && (self.session.mock
                || (capture["state"] == "Running"
                    && self
                        .live_owner
                        .as_ref()
                        .is_some_and(|o| o.state()["ready"] == true)))
        {
            Some("Running")
        } else {
            None
        };
        if let Some(next) = next {
            if next == "Idle" {
                self.session.ended_ns = Some(self.now());
            }
            self.session_transition(next, q)
                .map_err(|_| std::io::Error::other("Session transition unavailable"))?;
        }
        Ok(())
    }
}

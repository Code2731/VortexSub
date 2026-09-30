use crate::{runtime::Reply, transport::Outbox};
use echosub_audio_core::{AudioIdentity, RollingAudio};
#[cfg(windows)]
use echosub_capture_windows::CaptureOwner;
use serde_json::{json, Value};
use std::time::Instant;

pub struct CaptureRuntime {
    pub enabled: bool,
    state: &'static str,
    #[cfg(windows)]
    owner: Option<CaptureOwner>,
    identity: AudioIdentity,
    since: Instant,
    elapsed_s: f64,
    accepted_samples: u64,
    stats: Value,
    info: Value,
    error: Option<&'static str>,
    last_metrics: Instant,
}
impl CaptureRuntime {
    pub fn new(enabled: bool) -> Self {
        Self {
            enabled,
            state: "Idle",
            #[cfg(windows)]
            owner: None,
            identity: AudioIdentity {
                session_id: 1,
                epoch: 1,
            },
            since: Instant::now(),
            elapsed_s: 0.,
            accepted_samples: 0,
            stats: json!({}),
            info: Value::Null,
            error: None,
            last_metrics: Instant::now(),
        }
    }
    pub fn startable(&self) -> bool {
        matches!(self.state, "Idle" | "Stopped" | "Failed") && self.joined()
    }
    #[cfg(windows)]
    fn joined(&self) -> bool {
        self.owner.is_none()
    }
    #[cfg(not(windows))]
    fn joined(&self) -> bool {
        true
    }
    pub fn active(&self) -> bool {
        matches!(self.state, "Opening" | "Running" | "Stopping")
    }
    pub fn needs_stop(&self) -> bool {
        matches!(self.state, "Opening" | "Running")
    }
    pub fn start(&mut self, p: &Value, identity: AudioIdentity, origin: u64, q: &Outbox) -> Reply {
        if !self.enabled {
            return Err((
                "UNSUPPORTED_CAPABILITY",
                "Capture diagnostics require opt-in",
            ));
        }
        if !self.startable() {
            return Err(("INVALID_STATE", "Capture has not finished stopping"));
        }
        let device = match p.get("device_id") {
            None | Some(Value::Null) => None,
            Some(Value::String(id)) if !id.is_empty() && id.len() <= 2048 && !id.contains('\0') => {
                Some(id.clone())
            }
            _ => return Err(("INVALID_REQUEST", "Invalid render endpoint ID")),
        };
        #[cfg(not(windows))]
        {
            let _ = (device, identity, origin, q);
            Err(("UNSUPPORTED_CAPABILITY", "Windows is required"))
        }
        #[cfg(windows)]
        {
            self.owner = Some(CaptureOwner::start(device, identity, origin));
            self.identity = identity;
            self.since = Instant::now();
            self.last_metrics = self.since;
            self.elapsed_s = 0.;
            self.accepted_samples = 0;
            self.error = None;
            self.info = Value::Null;
            self.stats = json!({});
            self.state = "Opening";
            self.event(q)?;
            Ok(
                json!({"accepted":true,"epoch":identity.epoch,"input":"wasapi_loopback","live_asr":false}),
            )
        }
    }
    pub fn stop(&mut self, q: &Outbox) -> Reply {
        if !self.enabled {
            return Err((
                "UNSUPPORTED_CAPABILITY",
                "Capture diagnostics require opt-in",
            ));
        }
        if matches!(self.state, "Opening" | "Running") {
            self.state = "Stopping";
            self.request_stop();
            self.event(q)?;
        }
        Ok(json!({"accepted":true,"state":self.state,"awaiting_capture_join":!self.joined()}))
    }
    fn event(&self, q: &Outbox) -> Reply {
        q.publish("capture.state", self.value(), None)
            .map_err(|_| ("INTERNAL_ERROR", "Capture event unavailable"))?;
        Ok(json!({}))
    }
    pub fn value(&self) -> Value {
        json!({"enabled":self.enabled,"state":self.state,"session_id":self.identity.session_id,"epoch":self.identity.epoch,"elapsed_s":if self.active(){self.since.elapsed().as_secs_f64()}else{self.elapsed_s},"awaiting_capture_join":!self.joined(),"accepted_audio_s":self.accepted_samples as f64/16000.,"stats":self.stats,"endpoint":self.info,"error":self.error,"live_asr":false})
    }
    pub fn request_stop(&self) {
        #[cfg(windows)]
        if let Some(owner) = &self.owner {
            owner.request_stop();
        }
    }
    pub fn finish(&mut self) {
        self.request_stop();
        #[cfg(windows)]
        {
            self.owner.take();
        }
    }
    pub fn poll(&mut self, q: &Outbox, ring: &mut RollingAudio) -> std::io::Result<()> {
        #[cfg(not(windows))]
        {
            let _ = (q, ring);
            Ok(())
        }
        #[cfg(windows)]
        {
            let Some(owner) = &self.owner else {
                return Ok(());
            };
            let s = owner.stats();
            self.stats = json!({"packets":s.packets,"input_frames":s.input_frames,"normalized_frames":s.normalized_frames,"discontinuities":s.discontinuities,"first_qpc_100ns":s.first_qpc_100ns,"last_qpc_100ns":s.last_qpc_100ns,"packet_slots":echosub_capture_windows::PACKET_SLOTS,"normalized_queue_capacity":echosub_capture_windows::FRAME_QUEUE});
            if let Some(info) = owner.info() {
                self.info = json!({"device_id":info.device_id,"sample_rate":info.rate,"channels":info.channels,"channel_mask":info.mask,"selection_policy":"pinned_until_restart"});
            }
            let error = owner.error();
            let done = owner.finished();
            if let Some(error) = error {
                if self.state != "Failed" {
                    self.error = Some(error);
                    self.state = "Failed";
                    self.elapsed_s = self.since.elapsed().as_secs_f64();
                    self.event(q)
                        .map_err(|_| std::io::Error::other("Capture error event unavailable"))?;
                }
            } else if self.state == "Opening" && owner.ready() {
                self.state = "Running";
                self.event(q)
                    .map_err(|_| std::io::Error::other("Capture ready event unavailable"))?;
            }
            if self.state == "Running" {
                for _ in 0..echosub_capture_windows::FRAME_QUEUE {
                    let Ok(frame) = self.owner.as_ref().unwrap().frames.try_recv() else {
                        break;
                    };
                    if frame.identity != self.identity {
                        continue;
                    }
                    ring.append(frame.identity, frame.range.start, &frame.samples)
                        .map_err(|_| std::io::Error::other("Live PCM range rejected"))?;
                    self.accepted_samples += frame.samples.len() as u64;
                }
                if self.last_metrics.elapsed().as_secs_f64() >= 1. {
                    q.publish(
                        "capture.metrics",
                        self.value(),
                        Some("capture.metrics".into()),
                    )?;
                    self.last_metrics = Instant::now();
                }
            }
            if done {
                self.owner.take();
                self.elapsed_s = self.since.elapsed().as_secs_f64();
                if self.state != "Failed" {
                    if self.state == "Stopping" {
                        self.state = "Stopped";
                    } else {
                        self.state = "Failed";
                        self.error = Some("CAPTURE_OWNER_EXITED");
                    }
                    self.event(q)
                        .map_err(|_| std::io::Error::other("Capture final event unavailable"))?;
                }
            }
            Ok(())
        }
    }
}
impl Drop for CaptureRuntime {
    fn drop(&mut self) {
        self.finish();
    }
}

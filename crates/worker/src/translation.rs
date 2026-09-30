use super::{core_error, Reply, Runtime};
use crate::transport::Outbox;
use echosub_audio_core::SegmentIdentity;
use echosub_pipeline_core::{Apply, Outcome};
use echosub_translation::{
    http::{select_model, Failure},
    owner::{Output, Owner},
    Endpoint,
};
use serde_json::{json, Value};
use std::time::{Duration, Instant};

pub struct Translator {
    pub enabled: bool,
    pub owner: Option<Owner>,
    state: &'static str,
    selected: Option<String>,
    requested: Option<String>,
    models: Vec<String>,
    catalog_pending: bool,
    started: Option<Instant>,
    completed: u64,
    error: Option<String>,
}
impl Default for Translator {
    fn default() -> Self {
        Self {
            enabled: false,
            owner: None,
            state: "Unavailable",
            selected: None,
            requested: None,
            models: Vec::new(),
            catalog_pending: false,
            started: None,
            completed: 0,
            error: None,
        }
    }
}
impl Translator {
    pub fn value(&self, decoding: bool) -> Value {
        json!({"state":self.state,"enabled":self.enabled,"model_id":self.selected,
            "models":self.models,"in_flight":decoding,"catalog_pending":self.catalog_pending,
            "completed_jobs":self.completed,"last_error":self.error})
    }
}
impl Runtime {
    pub fn enable_http_translation(&mut self) {
        self.translator.enabled = true;
        self.core.set_translation_enabled(false).unwrap();
    }
    pub fn translation_command(&mut self, method: &str, params: &Value, q: &Outbox) -> Reply {
        if !self.translator.enabled {
            return Err(("UNSUPPORTED_CAPABILITY", "HTTP translation requires opt-in"));
        }
        if self.translation.is_some()
            || self.translator.catalog_pending
            || self.flight.is_some()
            || !self.pending_inputs.is_empty()
            || self.core.queue_lengths() != (0, 0, 0)
            || !self.capture.startable()
            || (self.session.enabled && self.session.state != "Idle")
        {
            return Err((
                "INVALID_STATE",
                "Configure translation only when all jobs are idle",
            ));
        }
        let params = params
            .as_object()
            .ok_or(("INVALID_REQUEST", "Expected parameters object"))?;
        if method == "disable_translation" {
            if !params.is_empty() {
                return Err(("INVALID_REQUEST", "Disable accepts no parameters"));
            }
            self.core
                .set_translation_enabled(false)
                .map_err(core_error)?;
            self.translator.owner.take();
            self.translator.state = "Unavailable";
            self.translator.selected = None;
            self.translator.requested = None;
            self.translator.models.clear();
            self.translator.error = None;
        } else {
            if params
                .keys()
                .any(|k| !matches!(k.as_str(), "endpoint" | "model_id"))
            {
                return Err(("INVALID_REQUEST", "Unknown translation configuration field"));
            }
            let endpoint = params
                .get("endpoint")
                .and_then(Value::as_str)
                .filter(|s| s.len() <= 256)
                .ok_or(("INVALID_REQUEST", "Local endpoint required"))?;
            let endpoint = Endpoint::parse(endpoint)
                .map_err(|_| ("INVALID_REQUEST", "Invalid numeric loopback endpoint"))?;
            let requested = match params.get("model_id") {
                None | Some(Value::Null) => None,
                Some(Value::String(s))
                    if !s.trim().is_empty()
                        && s.len() <= 256
                        && !s.chars().any(char::is_control) =>
                {
                    Some(s.clone())
                }
                _ => return Err(("INVALID_REQUEST", "Invalid model ID")),
            };
            let token = std::env::var("ECHOSUB_TRANSLATION_TOKEN").ok();
            let mut owner = Owner::new(endpoint, token.as_deref())
                .map_err(|_| ("INVALID_CONFIG", "HTTP client configuration rejected"))?;
            owner
                .models(Duration::from_secs(8))
                .map_err(|_| ("INTERNAL_ERROR", "HTTP owner unavailable"))?;
            self.core
                .set_translation_enabled(true)
                .map_err(core_error)?;
            self.translator.owner = Some(owner);
            self.translator.state = "Preparing";
            self.translator.selected = None;
            self.translator.requested = requested;
            self.translator.models.clear();
            self.translator.catalog_pending = true;
            self.translator.started = Some(Instant::now());
            self.translator.error = None;
        }
        q.publish("translator.state", self.translator.value(false), None)
            .map_err(|_| ("INTERNAL_ERROR", "Translator event unavailable"))?;
        Ok(json!({"accepted":true,"translator":self.translator.value(false)}))
    }
    pub fn poll_translation(&mut self, q: &Outbox) -> std::io::Result<()> {
        if !self.translator.enabled {
            return Ok(());
        }
        if self.core.cancellation().translation.is_some() {
            if let Some(owner) = &self.translator.owner {
                owner.cancel();
            }
        }
        if let Some(completion) = self.translator.owner.as_mut().and_then(Owner::poll) {
            if self.translator.catalog_pending {
                self.translator.catalog_pending = false;
                let selected = match completion.result {
                    Ok(Output::Models(ids)) => {
                        let result = select_model(&ids, self.translator.requested.as_deref());
                        self.translator.models = ids;
                        result
                    }
                    Err(e) => Err(e),
                    _ => Err(Failure::Transport),
                };
                match selected {
                    Ok(model) => {
                        self.translator.selected = Some(model);
                        self.translator.state = "Ready";
                        self.translator.error = None;
                    }
                    Err(e) => {
                        self.translator.state = "Failed";
                        self.translator.error = Some(format!("{e:?}"));
                    }
                }
                q.publish("translator.state", self.translator.value(false), None)?;
                self.translator.started = None;
            } else {
                let job = self
                    .translation
                    .take()
                    .ok_or_else(|| std::io::Error::other("Unexpected HTTP completion"))?;
                if completion.key != Some(job.key) {
                    return Err(std::io::Error::other("HTTP identity mismatch"));
                }
                let (outcome, error) = match completion.result {
                    Ok(Output::Text(text)) => (Outcome::Text(text), None),
                    Err(Failure::Cancelled) => (Outcome::Cancelled, Some("Cancelled".into())),
                    Err(e) => (Outcome::Failed, Some(format!("{e:?}"))),
                    _ => (Outcome::Failed, Some("UnexpectedOutput".into())),
                };
                self.apply_translation(job.key, outcome, error, q)?;
            }
        }
        if self.translation.is_none() && !self.translator.catalog_pending {
            if let Some(job) = self
                .core
                .next_translation(self.now())
                .map_err(|_| std::io::Error::other("Translation dispatch rejected"))?
            {
                self.translator.started = Some(Instant::now());
                let submitted = match (&mut self.translator.owner, &self.translator.selected) {
                    (Some(owner), Some(model)) if self.translator.state == "Ready" => owner
                        .translate(
                            &job,
                            model,
                            self.origin.elapsed().as_nanos().min(u64::MAX as u128) as u64,
                        )
                        .map_err(|e| format!("{e:?}")),
                    _ => Err("TranslatorUnavailable".into()),
                };
                match submitted {
                    Ok(()) => self.translation = Some(job),
                    Err(error) => {
                        self.apply_translation(job.key, Outcome::Failed, Some(error), q)?
                    }
                }
            }
        }
        Ok(())
    }
    fn apply_translation(
        &mut self,
        key: echosub_pipeline_core::TranslationKey,
        outcome: Outcome,
        error: Option<String>,
        q: &Outbox,
    ) -> std::io::Result<()> {
        let applied = self
            .core
            .complete_translation(key, outcome, self.now())
            .map_err(|_| std::io::Error::other("Translation completion rejected"))?;
        self.translator.completed += 1;
        self.translator.error = error.clone();
        let elapsed_s = self
            .translator
            .started
            .take()
            .map(|s| s.elapsed().as_secs_f64());
        q.publish("translation.completed", json!({"session_id":key.source.audio.session_id,
            "epoch":key.source.audio.epoch,"segment_id":key.source.segment_id,"source_revision":key.source.source_revision,
            "translation_request_id":key.request_id,"elapsed_s":elapsed_s,"applied":applied==Apply::Applied,"error":error}), None)?;
        if applied == Apply::Applied {
            let record = self
                .core
                .record(SegmentIdentity {
                    audio: key.source.audio,
                    segment_id: key.source.segment_id,
                })
                .ok_or_else(|| std::io::Error::other("Translation record unavailable"))?;
            q.publish(
                "translation.updated",
                json!({"history_version":self.core.version(),"record":self.wire_record(record)}),
                None,
            )?;
        }
        Ok(())
    }
}

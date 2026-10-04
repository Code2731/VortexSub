use super::{core_error, Reply, Runtime};
use crate::transport::Outbox;
use echosub_audio_core::SegmentIdentity;
use echosub_pipeline_core::{Apply, Outcome};
use echosub_translation::{
    http::{select_model, Failure},
    owner::{Output, Owner},
    Endpoint, PromptPolicy,
};
use serde_json::{json, Value};
use std::time::{Duration, Instant};

pub struct Translator {
    pub enabled: bool,
    pub owner: Option<Owner>,
    pub prompt_policy: PromptPolicy,
    pending_owner: Option<Owner>,
    pending_policy: Option<PromptPolicy>,
    state: &'static str,
    selected: Option<String>,
    requested: Option<String>,
    models: Vec<String>,
    catalog_pending: bool,
    started: Option<Instant>,
    completed: u64,
    error: Option<String>,
    started_preview: bool,
    preview_completed: u64,
}

#[cfg(test)]
mod tests {
    use super::*;
    use echosub_audio_core::{SampleRange, SegmentIdentity};
    use echosub_pipeline_core::AsrKind;

    fn owner(policy: PromptPolicy) -> Owner {
        Owner::new_with_policy(Endpoint::parse("http://127.0.0.1:1").unwrap(), None, policy)
            .unwrap()
    }
    fn stage(runtime: &mut Runtime) {
        runtime.translator.pending_owner = Some(owner(PromptPolicy::Original));
        runtime.translator.pending_policy = Some(PromptPolicy::Original);
        runtime.translator.catalog_pending = true;
        runtime.translator.state = "Preparing";
        runtime.translator.started = Some(Instant::now());
    }

    #[test]
    fn catalog_commit_rejection_retains_connection_and_can_retry_after_drain() {
        for previous in [false, true] {
            let mut runtime = Runtime::new(false, None, false);
            runtime.translator.enabled = true;
            if previous {
                runtime.translator.owner = Some(owner(PromptPolicy::IsolatedContext));
                runtime.translator.prompt_policy = PromptPolicy::IsolatedContext;
                runtime.translator.selected = Some("old/model".into());
                runtime.translator.models = vec!["old/model".into()];
            }
            // Inject a real outstanding reservation between staging and commit. The
            // production idle guards normally prevent this; do not add a public bypass.
            runtime.ring.append(runtime.epoch, 0, &[0.2; 512]).unwrap();
            let identity = SegmentIdentity {
                audio: runtime.epoch,
                segment_id: 1,
            };
            runtime
                .core
                .submit_asr(
                    identity,
                    SampleRange { start: 0, end: 512 },
                    AsrKind::Final,
                    &runtime.ring,
                    &mut runtime.pool,
                    0,
                )
                .unwrap();
            let asr = runtime.core.next_asr().unwrap();
            runtime
                .core
                .complete_asr(asr.key(), Outcome::Text("Keep moving.".into()), 1)
                .unwrap();
            let job = runtime.core.next_translation(2).unwrap().unwrap();
            runtime.translation = Some(job.clone());
            stage(&mut runtime);
            let q = Outbox::default();
            runtime
                .complete_translation_catalog(
                    Ok(("new/model".into(), vec!["new/model".into()])),
                    &q,
                )
                .unwrap();
            assert_eq!(
                runtime.translator.state,
                if previous { "Ready" } else { "Failed" }
            );
            assert_eq!(runtime.translator.error.as_deref(), Some("CommitRejected"));
            assert_eq!(runtime.translator.owner.is_some(), previous);
            assert_eq!(
                runtime.translator.selected.as_deref(),
                previous.then_some("old/model")
            );
            assert_eq!(
                runtime.translator.models,
                if previous { vec!["old/model"] } else { vec![] }
            );
            assert_eq!(
                runtime.translator.prompt_policy,
                if previous {
                    PromptPolicy::IsolatedContext
                } else {
                    PromptPolicy::Original
                }
            );
            assert!(!runtime.translator.configuring());
            assert!(runtime.translator.pending_owner.is_none());
            assert!(runtime.translator.pending_policy.is_none());
            assert!(runtime.translator.started.is_none());
            runtime.poll_translation(&q).unwrap();
            assert_eq!(
                runtime.state(&q)["translator"]["last_error"],
                "CommitRejected"
            );
            runtime
                .core
                .complete_translation(job.key, Outcome::Cancelled, 3)
                .unwrap();
            runtime.translation = None;
            stage(&mut runtime);
            runtime
                .complete_translation_catalog(
                    Ok(("new/model".into(), vec!["new/model".into()])),
                    &q,
                )
                .unwrap();
            assert_eq!(runtime.translator.state, "Ready");
            assert_eq!(runtime.translator.selected.as_deref(), Some("new/model"));
            assert_eq!(runtime.translator.prompt_policy, PromptPolicy::Original);
            assert!(runtime.translator.error.is_none());
            assert!(!runtime.translator.configuring());
        }
    }
}
impl Default for Translator {
    fn default() -> Self {
        Self {
            enabled: false,
            owner: None,
            prompt_policy: PromptPolicy::Original,
            pending_owner: None,
            pending_policy: None,
            state: "Unavailable",
            selected: None,
            requested: None,
            models: Vec::new(),
            catalog_pending: false,
            started: None,
            completed: 0,
            error: None,
            started_preview: false,
            preview_completed: 0,
        }
    }
}
impl Translator {
    pub fn configuring(&self) -> bool {
        self.catalog_pending
    }
    pub fn close_pending(&mut self) {
        self.pending_owner.take();
        self.pending_policy.take();
        self.catalog_pending = false;
    }
    fn reject_pending(&mut self, error: String) {
        self.close_pending();
        self.state = if self.owner.is_some() && self.selected.is_some() {
            "Ready"
        } else {
            "Failed"
        };
        self.error = Some(error);
    }
    pub fn value(&self, decoding: bool) -> Value {
        json!({"state":self.state,"enabled":self.enabled,"model_id":self.selected,
            "models":self.models,"in_flight":decoding,"catalog_pending":self.catalog_pending,
            "completed_jobs":self.completed,"preview_completed_jobs":self.preview_completed,"last_error":self.error,
            "isolated_context":self.prompt_policy == PromptPolicy::IsolatedContext,
            "input_profile":self.prompt_policy.profile_name(),
            "pending_input_profile":self.pending_policy.map(PromptPolicy::profile_name)})
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
            self.translator.close_pending();
            self.translator.state = "Unavailable";
            self.translator.selected = None;
            self.translator.requested = None;
            self.translator.models.clear();
            self.translator.error = None;
        } else {
            if params.keys().any(|k| {
                !matches!(
                    k.as_str(),
                    "endpoint" | "model_id" | "isolated_context" | "input_profile"
                )
            }) {
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
            let selected_policy = match params.get("input_profile") {
                None => self.translator.prompt_policy,
                Some(Value::String(name)) => match name.as_str() {
                    "standard" => PromptPolicy::Original,
                    "qwen-greedy" => PromptPolicy::QwenGreedy,
                    "hymt2-greedy" => PromptPolicy::HyMt2Greedy,
                    _ => return Err(("INVALID_REQUEST", "Unknown translation input profile")),
                },
                _ => return Err(("INVALID_REQUEST", "input_profile must be a string")),
            };
            let prompt_policy = match params.get("isolated_context") {
                None => selected_policy,
                Some(Value::Bool(true)) if selected_policy.profile_name() == "standard" => {
                    PromptPolicy::IsolatedContext
                }
                Some(Value::Bool(true)) => {
                    return Err((
                        "INVALID_CONFIG",
                        "Selected profile cannot combine with isolated context",
                    ))
                }
                Some(Value::Bool(false)) if selected_policy.profile_name() == "standard" => {
                    PromptPolicy::Original
                }
                Some(Value::Bool(false)) => selected_policy,
                _ => return Err(("INVALID_REQUEST", "isolated_context must be boolean")),
            };
            let mut owner = Owner::new_with_policy(endpoint, token.as_deref(), prompt_policy)
                .map_err(|_| ("INVALID_CONFIG", "HTTP client configuration rejected"))?;
            owner
                .models(Duration::from_secs(8))
                .map_err(|_| ("INTERNAL_ERROR", "HTTP owner unavailable"))?;
            // Stage the catalog separately. Keep the current connection/profile until commit.
            self.translator.pending_owner = Some(owner);
            self.translator.pending_policy = Some(prompt_policy);
            self.translator.state = "Preparing";
            self.translator.requested = requested;
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
        let completion = if self.translator.catalog_pending {
            self.translator.pending_owner.as_mut().and_then(Owner::poll)
        } else {
            self.translator.owner.as_mut().and_then(Owner::poll)
        };
        if let Some(completion) = completion {
            if self.translator.catalog_pending {
                let selected = match completion.result {
                    Ok(Output::Models(ids)) => {
                        let result = select_model(&ids, self.translator.requested.as_deref());
                        result.map(|model| (model, ids))
                    }
                    Err(e) => Err(e),
                    _ => Err(Failure::Transport),
                };
                self.complete_translation_catalog(selected, q)?;
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
                self.translator.started_preview = self
                    .core
                    .record(SegmentIdentity {
                        audio: job.key.source.audio,
                        segment_id: job.key.source.segment_id,
                    })
                    .is_some_and(|r| r.translation_is_preview);
                q.publish("translation.started", json!({"worker_at_s":self.now() as f64/1e9,
                    "session_id":job.key.source.audio.session_id,"epoch":job.key.source.audio.epoch,
                    "segment_id":job.key.source.segment_id,"source_revision":job.key.source.source_revision,
                    "translation_request_id":job.key.request_id,"preview":self.translator.started_preview}),None)?;
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
    fn complete_translation_catalog(
        &mut self,
        selected: Result<(String, Vec<String>), Failure>,
        q: &Outbox,
    ) -> std::io::Result<()> {
        match (selected, self.translator.pending_policy) {
            (Ok((model, ids)), Some(policy))
                if self.translator.pending_owner.is_some() && policy.accepts_model(&model) =>
            {
                // Validate the final core transition before replacing any live connection.
                // A rejected configuration is recoverable; only transport publication errors
                // propagate out of poll and terminate the worker.
                if self.core.set_translation_enabled(true).is_err() {
                    self.translator.reject_pending("CommitRejected".into());
                } else {
                    self.translator.owner = self.translator.pending_owner.take();
                    self.translator.prompt_policy = policy;
                    self.translator.pending_policy = None;
                    self.translator.selected = Some(model);
                    self.translator.models = ids;
                    self.translator.state = "Ready";
                    self.translator.error = None;
                    self.translator.catalog_pending = false;
                }
            }
            (Ok(_), _) => self
                .translator
                .reject_pending("ModelProfileMismatch".into()),
            (Err(error), _) => self.translator.reject_pending(format!("{error:?}")),
        }
        self.translator.started = None;
        q.publish("translator.state", self.translator.value(false), None)
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
        if self.translator.started_preview {
            self.translator.preview_completed += 1;
        }
        self.translator.error = error.clone();
        let elapsed_s = self
            .translator
            .started
            .take()
            .map(|s| s.elapsed().as_secs_f64());
        q.publish("translation.completed", json!({"worker_at_s":self.now() as f64/1e9,"session_id":key.source.audio.session_id,
            "epoch":key.source.audio.epoch,"segment_id":key.source.segment_id,"source_revision":key.source.source_revision,
            "translation_request_id":key.request_id,"elapsed_s":elapsed_s,"preview":self.translator.started_preview,"applied":applied==Apply::Applied,"error":error}), None)?;
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
                json!({"worker_at_s":self.now() as f64/1e9,"history_version":self.core.version(),"record":self.wire_record(record)}),
                None,
            )?;
        }
        Ok(())
    }
}

//! One dedicated HTTP thread; one command/result slot, reservation until poll.
use crate::{
    http::{Cancellation, Failure, HttpClient},
    prepare, Endpoint, Prepared,
};
use echosub_pipeline_core::{TranslationJob, TranslationKey};
use std::{
    sync::mpsc,
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

pub enum Output {
    Models(Vec<String>),
    Text(String),
    Bypass,
}
pub struct Completion {
    pub key: Option<TranslationKey>,
    pub result: Result<Output, Failure>,
}
enum Operation {
    Models(Duration),
    Translate(Prepared),
}
struct Command {
    operation: Operation,
    submitted: Instant,
    cancellation: Cancellation,
}
#[derive(Debug, PartialEq, Eq)]
pub enum SubmitError {
    Busy,
    Closed,
    Invalid(Failure),
}
pub struct Owner {
    sender: Option<mpsc::SyncSender<Command>>,
    receiver: mpsc::Receiver<Completion>,
    active: Option<(Cancellation, Option<TranslationKey>)>,
    thread: Option<JoinHandle<()>>,
}
impl Owner {
    pub fn new(endpoint: Endpoint, token: Option<&str>) -> Result<Self, Failure> {
        let client = HttpClient::new(endpoint, token)?;
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .map_err(|_| Failure::Transport)?;
        let (sender, commands) = mpsc::sync_channel::<Command>(1);
        let (results, receiver) = mpsc::sync_channel(1);
        let thread = thread::spawn(move || {
            while let Ok(command) = commands.recv() {
                let cancellation = command.cancellation;
                let waited = command.submitted.elapsed();
                let (key, result) = match command.operation {
                    Operation::Models(budget) => (
                        None,
                        runtime
                            .block_on(client.models(budget.saturating_sub(waited), &cancellation))
                            .map(Output::Models),
                    ),
                    Operation::Translate(Prepared::Bypass(key)) => (
                        Some(key),
                        if cancellation.requested() {
                            Err(Failure::Cancelled)
                        } else {
                            Ok(Output::Bypass)
                        },
                    ),
                    Operation::Translate(Prepared::Send(mut request)) => {
                        request.remaining = request.remaining.saturating_sub(waited);
                        (
                            Some(request.key),
                            runtime
                                .block_on(client.translate(&request, &cancellation))
                                .map(Output::Text),
                        )
                    }
                };
                if results.try_send(Completion { key, result }).is_err() {
                    break;
                }
            }
        });
        Ok(Self {
            sender: Some(sender),
            receiver,
            active: None,
            thread: Some(thread),
        })
    }
    pub fn models(&mut self, budget: Duration) -> Result<(), SubmitError> {
        self.submit(Operation::Models(budget), Instant::now())
    }
    pub fn translate(
        &mut self,
        job: &TranslationJob,
        model: &str,
        now_ns: u64,
    ) -> Result<(), SubmitError> {
        if self.active.is_some() {
            return Err(SubmitError::Busy);
        }
        let submitted = Instant::now();
        let request = prepare(job, model, now_ns).map_err(|e| SubmitError::Invalid(e.into()))?;
        self.submit(Operation::Translate(request), submitted)
    }
    fn submit(&mut self, operation: Operation, submitted: Instant) -> Result<(), SubmitError> {
        if self.active.is_some() {
            return Err(SubmitError::Busy);
        }
        let cancellation = Cancellation::default();
        let key = match &operation {
            Operation::Models(_) => None,
            Operation::Translate(Prepared::Bypass(key)) => Some(*key),
            Operation::Translate(Prepared::Send(request)) => Some(request.key),
        };
        let command = Command {
            operation,
            submitted,
            cancellation: cancellation.clone(),
        };
        self.sender
            .as_ref()
            .ok_or(SubmitError::Closed)?
            .try_send(command)
            .map_err(|_| SubmitError::Closed)?;
        self.active = Some((cancellation, key));
        Ok(())
    }
    pub fn cancel(&self) {
        if let Some((active, _)) = &self.active {
            active.cancel();
        }
    }
    pub fn poll(&mut self) -> Option<Completion> {
        match self.receiver.try_recv() {
            Ok(completion) => {
                self.active = None;
                Some(completion)
            }
            Err(mpsc::TryRecvError::Disconnected) => {
                self.active.take().map(|(_, key)| Completion {
                    key,
                    result: Err(Failure::Transport),
                })
            }
            _ => None,
        }
    }
}
impl Drop for Owner {
    fn drop(&mut self) {
        self.cancel();
        self.sender.take();
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

use crate::{completion, models, Endpoint, Error, Request, CONNECT_TIMEOUT, MAX_RESPONSE_BYTES};
use reqwest::{header, redirect, Client};
use std::{
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::{Duration, Instant},
};
use tokio::sync::Notify;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Failure {
    Contract(Error),
    Status(u16),
    Connection,
    Transport,
    Cancelled,
    Deadline,
    InvalidToken,
}
impl From<Error> for Failure {
    fn from(error: Error) -> Self {
        Self::Contract(error)
    }
}
#[derive(Clone, Default)]
pub struct Cancellation(Arc<CancelState>);
#[derive(Default)]
struct CancelState {
    cancelled: AtomicBool,
    notify: Notify,
}
impl Cancellation {
    pub fn cancel(&self) {
        self.0.cancelled.store(true, Ordering::Release);
        self.0.notify.notify_one();
    }
    pub fn requested(&self) -> bool {
        self.0.cancelled.load(Ordering::Acquire)
    }
    async fn wait(&self) {
        while !self.requested() {
            self.0.notify.notified().await;
        }
    }
}

pub struct HttpClient {
    client: Client,
    endpoint: Endpoint,
}
impl HttpClient {
    pub fn new(endpoint: Endpoint, token: Option<&str>) -> Result<Self, Failure> {
        let mut headers = header::HeaderMap::new();
        if let Some(token) = token {
            if token.is_empty()
                || token.len() > 8192
                || token.bytes().any(|b| !b.is_ascii_graphic())
            {
                return Err(Failure::InvalidToken);
            }
            let mut value = header::HeaderValue::from_str(&format!("Bearer {token}"))
                .map_err(|_| Failure::InvalidToken)?;
            value.set_sensitive(true);
            headers.insert(header::AUTHORIZATION, value);
        }
        let client = Client::builder()
            .no_proxy()
            .redirect(redirect::Policy::none())
            .connect_timeout(CONNECT_TIMEOUT)
            .default_headers(headers)
            .build()
            .map_err(|_| Failure::Transport)?;
        Ok(Self { client, endpoint })
    }
    pub async fn models(
        &self,
        budget: Duration,
        cancellation: &Cancellation,
    ) -> Result<Vec<String>, Failure> {
        let bytes = self.execute(None, budget, cancellation).await?;
        Ok(models(&bytes)?)
    }
    pub async fn translate(
        &self,
        request: &Request,
        cancellation: &Cancellation,
    ) -> Result<String, Failure> {
        let bytes = self
            .execute(Some(&request.body), request.remaining, cancellation)
            .await?;
        Ok(completion(&bytes)?)
    }
    async fn execute(
        &self,
        body: Option<&serde_json::Value>,
        budget: Duration,
        cancellation: &Cancellation,
    ) -> Result<Vec<u8>, Failure> {
        if budget.is_zero() {
            return Err(Failure::Deadline);
        }
        let end = Instant::now()
            .checked_add(budget)
            .ok_or(Failure::Deadline)?;
        let work = async {
            for attempt in 0..2 {
                let remaining = end
                    .checked_duration_since(Instant::now())
                    .ok_or(Failure::Deadline)?;
                let builder = match body {
                    Some(body) => self.client.post(self.endpoint.completion_url()).json(body),
                    None => self.client.get(self.endpoint.models_url()),
                }
                .timeout(remaining);
                let sent = builder.send().await;
                let mut response = match sent {
                    Ok(response) => response,
                    Err(error) if error.is_connect() => {
                        if attempt == 0 && retry_fits(end, Duration::from_millis(100)) {
                            tokio::time::sleep(Duration::from_millis(100)).await;
                            continue;
                        }
                        return Err(Failure::Connection);
                    }
                    Err(error) if error.is_timeout() => return Err(Failure::Deadline),
                    Err(_) => return Err(Failure::Transport),
                };
                let status = response.status();
                if !status.is_success() {
                    let delay = if status.as_u16() == 429 {
                        response
                            .headers()
                            .get(header::RETRY_AFTER)
                            .and_then(|v| v.to_str().ok())
                            .and_then(|v| v.parse::<u64>().ok())
                            .filter(|n| *n <= 60)
                            .map(Duration::from_secs)
                    } else if status.is_server_error() {
                        Some(Duration::from_millis(100))
                    } else {
                        None
                    };
                    if attempt == 0 {
                        if let Some(delay) = delay.filter(|delay| retry_fits(end, *delay)) {
                            drop(response);
                            tokio::time::sleep(delay).await;
                            continue;
                        }
                    }
                    return Err(Failure::Status(status.as_u16()));
                }
                if response.headers().contains_key(header::CONTENT_ENCODING) {
                    return Err(Failure::Contract(Error::InvalidResponse));
                }
                if response
                    .content_length()
                    .is_some_and(|n| n > MAX_RESPONSE_BYTES as u64)
                {
                    return Err(Failure::Contract(Error::OversizedResponse));
                }
                let mut bytes = Vec::new();
                while let Some(chunk) = response.chunk().await.map_err(|error| {
                    if error.is_timeout() {
                        Failure::Deadline
                    } else {
                        Failure::Transport
                    }
                })? {
                    if bytes.len().saturating_add(chunk.len()) > MAX_RESPONSE_BYTES {
                        return Err(Failure::Contract(Error::OversizedResponse));
                    }
                    bytes.extend_from_slice(&chunk);
                }
                return Ok(bytes);
            }
            unreachable!()
        };
        tokio::select! {
            biased;
            _ = cancellation.wait() => Err(Failure::Cancelled),
            result = tokio::time::timeout(budget, work) => result.unwrap_or(Err(Failure::Deadline)),
        }
    }
}
fn retry_fits(end: Instant, delay: Duration) -> bool {
    end.checked_duration_since(Instant::now())
        .is_some_and(|remaining| remaining > delay + Duration::from_millis(100))
}
pub fn select_model(ids: &[String], requested: Option<&str>) -> Result<String, Failure> {
    match requested {
        Some(id) if ids.iter().any(|actual| actual == id) => Ok(id.to_owned()),
        None if ids.len() == 1 => Ok(ids[0].clone()),
        _ => Err(Error::InvalidModel.into()),
    }
}

#[cfg(test)]
mod tests;

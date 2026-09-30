# Local translation contract and HTTP owner (T03-01a/b)

`crates/translation/` implements provider-independent request/response validation
and a bounded local HTTP owner.
It consumes the existing `TranslationJob`, retaining its full source identity,
request ID and remaining monotonic deadline. The pipeline supplies final jobs and
same-epoch context; this crate performs no scheduling or history application.

## Request

- Numeric loopback HTTP only: `http://127.0.0.1:1234/v1/` or
  `http://[::1]:8080/v1/`. Root and `/v1` normalize to one `/v1` prefix.
  DNS names, credentials, queries, fragments and other paths are rejected.
- Model IDs are read from bounded `/v1/models` JSON; selection must use an actual
  returned ID. Catalog limit: 128 unique IDs, each at most 256 bytes.
- Same-language jobs return Bypass without requiring a model or server call.
- Current source is preserved. Source+context budget is 2,000 Unicode scalar
  values; each text also respects the pipeline's 4,096 UTF-8 byte ceiling.
  Scalars are neither token counts nor grapheme clusters.
- Keep the latest two source contexts, oldest first, totaling at most 600 scalars.
  Remove whole oldest contexts until within budget. Never truncate source.
- User message is JSON text with source/context/languages. System rules ask for
  translation only, preserving numbers and negation. Stream=false,
  temperature=0.2, max_tokens=256; no tools. Prompt wording does not guarantee
  semantic correctness or resistance to misleading subtitle content.

## Response and transport boundary

Body limit is 256 KiB; translation limit is 4,096 UTF-8 bytes. Only string content
with finish_reason=stop is accepted. Empty/NUL/oversized output, tool/function
calls, explicit refusal and truncated/unknown completion reasons fail.
Reasoning fields are never used as translation. Explanations or semantic errors
inside otherwise valid plain text still require quality evaluation.

The HTTP client disables proxies, redirects and automatic compression. It bounds
both Content-Length and streamed/chunked bytes while reading. Connection timeout
is 2 s; headers, body, retry delay and attempts share the remaining job budget.
Preparation and thread scheduling consume that budget too. Transient connection
failures and 5xx may retry once with 0.1 s delay; 429 accepts numeric Retry-After
only when it fits. HTTP-date Retry-After and context-error retry remain unsupported.
401/404, schema failures and truncated responses are terminal.

An optional bearer token is supplied in memory; headers are marked sensitive.
Errors expose only typed categories/status, without server bodies or token text.
This does not implement OS secret storage. Default features exclude TLS/proxy
discovery; only the validated local HTTP endpoints are supported.

`Owner` has one command slot, one completion slot and one active reservation.
Cancellation drops the client future and interrupts retry waits; reservation is
held until completion is polled. Owner destruction cancels and joins its thread.
This cannot guarantee cancellation of inference already executing at the server.
Completions retain the full source key/request ID. Worker integration must apply
results through the pipeline to reject stale epochs; it is the next round.

## Validation and next step

`cargo test -p echosub-translation --locked --offline` runs 20 contract/local HTTP
fixtures, including redirects, status/retries, body stalls, chunked size limits,
cancellation and owner lifetime. `scripts/probe-translation.ps1 -Contract -Offline`
uses the Rust owner with an installed llama-server and existing pinned model.
Without `-Contract`, the earlier C# baseline probe remains available.
See [T03-01b evidence](evidence/T03-01b-windows-translation-http.md).
Next: worker final-job dispatch/cancellation/history, followed by UI integration.

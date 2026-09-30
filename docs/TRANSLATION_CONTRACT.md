# Local translation contract (T03-01a)

`crates/translation/` implements provider-independent request/response validation.
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

The future HTTP owner must disable proxies/redirects, bound bytes while reading,
use connection timeout 2 s and the job's remaining overall budget, and validate
the full key again through pipeline completion. No HTTP calls, retries, secrets,
worker commands or UI translation are connected in this round.

## Validation and next step

`cargo test -p echosub-translation --locked --offline` runs eight deterministic
fixtures covering endpoints, Unicode/budgets, escaping, bypass/deadline, catalog
and response failures. Windows checks are recorded in
[T03-01a evidence](evidence/T03-01a-windows-translation-contract.md).
Next: bounded HTTP owner, model selection and connectivity diagnostics,
then final-job dispatch/cancellation/history and UI integration.

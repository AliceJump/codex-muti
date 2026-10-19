# Codex Pool architecture

Status: proposed architecture with Phase 0 acceptance gates. Date: 2026-10-12 (Asia/Shanghai).

## Responsibility boundary

**Codex owns the workflow. The proxy only owns model access.**

Official CLI/Desktop retains Session, Context, Compaction, Resume, tools, Shell, MCP, Git, approvals and the agent loop. The daemon never executes tools, rewrites a rollout or controls a worktree. A single optional `mcodex` wrapper supplies provider configuration and a local proxy credential, then delegates to the official executable without interpreting its output.


```text
Official Codex CLI/Desktop
    -> loopback Responses HTTP/SSE
    -> Proxy Daemon
         Account Store / OAuth / Refresh
         Account Pool / Quota / Sticky Scheduler
         Responses transport / Local control API / SSE state feed
    -> selected account's authorized model endpoint

Dashboard and Windows floating window -> local control API/state feed
```

## Evidence and target versions

Initial test target: installed `codex-cli 0.157.1`. Source investigation baseline: `openai/codex` commit `6c372fbef45f730b6d8aa727b0ec8332365588db`, dated 2026-10-12. These are separate version identities; master evidence is not proof that the installed binary supports every field. Record executable version, provider config and source revision in each acceptance run.

Verified source findings:

- Custom providers expose `base_url`, `wire_api="responses"`, credential sources and `supports_websockets`. [Provider definition](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/model-provider-info/src/lib.rs#L139).
- Codex builds `store:false`, `stream:true` requests and requests encrypted reasoning. Non-OpenAI provider handling preserves encrypted reasoning/compaction items. [Request construction](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/core/src/client.rs#L1005).
- WS can send incremental input with `previous_response_id`; HTTP sends the current local input window, which may already be compacted. [WS continuation](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/core/src/client.rs#L2095).
- Compaction is capability dependent; current source has local and Responses V2 paths. Do not infer `/responses/compact` from general API documentation. [Compaction routing](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/core/src/tasks/compact.rs#L63).
- Quota parsers support multiple limit families and primary/secondary windows. [Quota parser](https://github.com/openai/codex/blob/6c372fbef45f730b6d8aa727b0ec8332365588db/codex-rs/codex-api/src/rate_limits.rs).

Official third-party route supports independent account registrations, dynamic client IDs and PKCE. It uses public Responses endpoints, not ChatGPT backend-api. HTTP omits previous_response_id; WS continuation is scoped to the authenticated connection. [Overview](https://developers.openai.com/siwc/token-sharing-open-source), [Accounts](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions), [Inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), [Limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).

These documents establish a supported integration candidate, not completed login, quota compatibility or cross-account encrypted-state portability. Real-account gates remain pending.

## Technology and reuse decisions

Use .NET 10 for the daemon and core library, ASP.NET Core/Kestrel for loopback HTTP, HttpClient with response-header streaming for upstream. Windows has the SDK/runtime installed; native DPAPI and Windows UI interoperability avoid a second credential helper runtime. No general-purpose agent SDK is needed.

Dashboard is served by the daemon as a small local UI. The native window will use a Windows .NET client; prototype WinUI 3 Desktop Acrylic versus WPF/DWM before selecting its shell. This decision does not affect daemon APIs. Native UI is deferred until protocol and real-account gates pass.

CLIProxyAPI is a reference/candidate module source. Its session affinity, broad credential retries and translation must be audited against this narrower contract. Do not adopt its defaults or embed its agent/client management. Initial core uses a narrow Responses transport and existing platform primitives; reuse of OAuth helpers is conditional on support for the official dynamic-registration route and compatible licensing. [Candidate configuration](https://github.com/router-for-me/CLIProxyAPI/blob/main/config.example.yaml). Pin any reused source and retain notices.

## Account ownership and storage

Account identity is an opaque local ID bound to validated issuer/subject/workspace/issued-client identity. Email is a display hint, never the unique key. One local host ID is stable across restarts; distinct registrations retain their own issued client ID and credentials.

`AccountRuntime` owns authorization, refresh serialization and current credentials. Its public view contains name, plan, enabled, paused, draining, auth health, cooldown, quota snapshots and update times. It cannot serialize secrets. Windows storage uses DPAPI CurrentUser plus restricted local filesystem permissions outside the repository, with atomic replacement. No plaintext fallback. The daemon is the sole credential writer; a single-instance OS lock prevents duplicate refresh owners.

Refresh is single-flight per registration. After acquiring the lock, read the current credential generation, refresh once, validate identity, persist the entire replacement, then publish the generation. Temporary transport errors preserve credentials and apply bounded backoff. Explicit invalid grant/reused/revoked requires reauthorization. A successful server rotation followed by a persistence crash can require login again; never promise crash-proof token rotation or blindly retry a consumed refresh token.

Production tokens never enter Codex config/environment: Codex receives a separate local proxy credential. Test harnesses use synthetic credentials and isolated Codex profiles; existing user auth.json is never read, copied or changed.

## Scheduling and concurrency

The initial product has one global sticky active registration for all connected Codex sessions and subagents. This matches the single radio selector. Future per-session routing would be a separate explicit mode, not an implicit TTL binding.

Selection atomically returns a request lease: account ID, credential generation, selection epoch and immutable upstream authorization. Subsequent manual activation only affects new leases. Existing streams finish on their leased account. UI must distinguish the selected account for new requests from accounts with in-flight streams.

Manual activation increments the routing epoch and makes the chosen eligible account sticky. A late quota/error update from an older request updates that account only; it may replace active only if the failed account/epoch still owns active routing. This prevents A's completion from undoing a manual B selection.

Eligibility requires enabled, not paused/draining, usable auth, expired cooldown, compatible model and known quota below configured thresholds when available. Unknown/stale quota is represented explicitly; initial policy permits requests with unknown quota but never reports it as zero usage. If all accounts are excluded, return a bounded structured error and earliest known reset; never rotate forever or secretly relax thresholds.

Pause: exclude new requests. Drain: exclude new requests while existing streams finish. Disable: durable administrative exclusion. Resume clears pause; undrain is explicit. Auto-switch OFF disables automatic cross-account replacement; explicit activation still works. Recovered standby accounts do not reclaim active.

## Responses forwarding and retry contract

First release uses HTTP/SSE; `supports_websockets=false`. Do not implement a WS continuation cache until a separate compatibility gate passes. Catalog, compaction and other model routes are added only after observing the target client's actual requests. The initial supported route is POST `/v1/responses`.

Preserve request input, tool declarations, tool results and opaque encrypted items. Never delete/replace encrypted compaction or synthesize missing history. No payload transcript is persisted. Validate unsupported server continuation explicitly rather than converting incremental input into a guessed full conversation.

There is no generic HTTP retry handler for inference POST. Each account is attempted at most once per inbound request. Only a structured, verified auth/quota rejection before downstream commitment can authorize a different account. Ordinary 429, 5xx, timeout, network ambiguity and client cancellation cannot authorize transparent failover. One bounded same-account refresh may recover a 401 before commitment; permanent auth failures can exclude the account.

`committed` is a monotonic request-local latch set **before the first downstream body/event write**. `response.created`, output-item events and tool-argument events count; text deltas are not the only boundary. Initial implementation emits no pre-bootstrap keepalive. After commitment forward the upstream error/end-of-stream or abort on a transport interruption. Do not invent successful completion or append a different account's stream. Cancellation aborts upstream immediately. Bound pre-bootstrap buffers and event-frame parsing; malformed/unbounded input fails without replay.

The proxy guarantees it never replays a committed request. System-level no-duplicate tool effects also require testing Codex's own recovery with side-effect counters. The proxy never deduplicates or executes tools.

## Account-bound state and Phase 0 gate

Treat response IDs, WS state, `X-Codex-Turn-State`, encrypted reasoning/compaction, cache affinity and model entitlement as independent mechanisms. A local Codex thread ID is not automatically an upstream conversation ID.

- Disable WS to avoid delta-only requests in the first release.
- Never assume encrypted reasoning/compaction from A can be accepted by B.
- Inspect account-bound routing headers in the real target version; clear only verified account-bound transport state at an account boundary, not Codex local session identity.
- Cache reuse is a performance observation, not a correctness prerequisite. Do not claim shared cross-account cache or rewrite session/cache fields without a tested contract.
- Until real encrypted-state and compacted-state portability gates pass, multi-account production switching is gated. Synthetic tests can exercise scheduler and transport but do not unlock the gate.

If unmodified official Codex cannot rebuild a valid next request at a boundary, consider a small upstream compatibility patch limited to clearing bound continuation and rebuilding from Codex-owned local history. Keep all account scheduling and OAuth out of Codex. If local retained history is insufficient, record the failure and revise scope instead of silently losing context.

## Local API and UI synchronization

Bind `127.0.0.1:3210` only. LAN is unsupported in the initial release. Protect model/control endpoints with a local credential and browser control sessions with same-origin/CSRF protection. Validate Host and Origin, disallow wildcard CORS, and never include secrets in URLs or events. `/health` may expose only minimal readiness without authentication.

Planned routes:

| Route | Purpose |
|---|---|
| GET /health | Minimal readiness |
| GET /status, /accounts, /usage | Sanitized snapshots |
| GET /events | Authenticated SSE snapshots with monotonic revision |
| POST /accounts/login | Start interactive authorization |
| GET /oauth/callback | Validate state/nonce/PKCE and finish login |
| POST /accounts/:id/activate | Change sticky routing for new requests |
| POST /accounts/:id/pause, /resume | Pause/resume admission |
| POST /accounts/:id/drain, /undrain | Drain control |
| POST /accounts/:id/enable, /disable | Administrative admission control |
| PUT /settings | Auto-switch and quota thresholds |
| POST /v1/responses | Model request forwarding |

Login state/authorization URLs are returned only to the initiating trusted local client and are never logged. UI streams carry public status, request metadata and switch reasons. Subscribers receive a full snapshot on reconnect; slow clients have bounded queues and cannot backpressure model streams. Manual commands return the committed revision; radio selection updates from daemon authority, not an optimistic local-only toggle.

The native floating window offers radios, quota, active/auth state, full/compact/capsule modes, tray hide/show, a configurable non-conflicting hotkey, topmost, position restore and per-monitor DPI. It closes independently of the daemon. Desktop Acrylic supplies live desktop blur; Mica is a wallpaper-tinted alternative. Preserve readable solid fallback, restore within a connected monitor's work area, and test display disconnects. [Windows materials](https://learn.microsoft.com/en-us/windows/apps/develop/ui/materials).

## Observability and acceptance

Retain a bounded metadata-only request/switch ring: generated request ID, timestamp, opaque session hash if needed, account local ID, model, duration, HTTP status, quota snapshot and categorized failover reason. Never log upstream bodies, prompt text, tool arguments/results, token data, OAuth callback query or arbitrary error messages. Disable HTTP request logging of credential-bearing paths. Exceptions are classified into controlled codes.

Mocks verify transport forwarding, cancellation, retry boundaries, scheduler, refresh serialization and UI revisions. Real acceptance separately covers independent OAuth/rotation, quota entitlement, A/B encrypted state, compact/resume/long sessions, official CLI/Desktop behavior and cache observations. Tool-effect counters belong to official Codex test tools, not the daemon. See implementation-plan.md for release gates and per-phase commits.

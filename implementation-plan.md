# Implementation plan

Date: 2026-10-12 (Asia/Shanghai). Phases are separate commits; research artifacts stay in the primary checkout's ignored `.research/`. Architecture decisions are in architecture.md.

> 状态指针（2026-10-14）：原生多账户**核心**已通过小范围真实验收，完整发布门槛未清；ChatGPT Desktop 验收仍待完成；macOS 为原型。进行中的工程草稿见 README 中的 PR #6 / #7 / #8（分 PR 审查；本地可组合部署）。详见 [docs/native-acceptance-results.md](docs/native-acceptance-results.md)。


## Phase 0 — protocol and feasibility gate

Before product UI or automatic production switching:

1. Pin installed CLI (`0.157.1` initially), source evidence and actual provider fields.
2. Implement an isolated local fixture upstream and the narrow Responses/SSE transport. Synthetic accounts exercise failover without credential access.
3. Run the official executable against localhost: plain response, tool call/result, failure before/after first event, cancellation, resume and compaction where the target client supports the chosen route. Record sanitized request shapes and counters, never full prompt transcripts in product logs.
4. Confirm official third-party OAuth dynamic registration/PKCE/OIDC requirements before implementing login. Build a local interactive login path; human login is required, passwords/tokens never enter this chat.
5. With separately authorized A/B, verify grant isolation and refresh rotation, plain and encrypted-context A->B, tool-result A->B, compacted A->B, resume/long conversation and cache behavior.
6. Independently verify CLI and Desktop provider support; CLI success is not a Desktop pass.

Mock pass does not mark live-account gates passed. If live accounts are unavailable, finish the runnable fixture, secure login path and instructions, mark the gate pending and do not enable production auto-switch. If encrypted/compacted migration fails, preserve the failure and investigate the permitted tiny Codex patch before scheduler/UI release.

## Phase 1 — single-account proxy

Loopback-only daemon, authenticated local model endpoint, one authorized account, bounded SSE streaming and cancellation. Official Codex retains all tools and history. Smoke test a real model response and tool/result continuation. No generic retries or prompt logging. Commit only when its stated tests pass; label live validation separately.

## Phase 2 — N-account storage and manual selection

Independent registrations/credentials in DPAPI CurrentUser storage, duplicate-registration validation, atomic persistence, sanitized status, manual global sticky activation and request leases. Existing streams remain pinned. Store no credentials in the repository or Codex auth.json. Unlock real switching only after Phase 0 migration gates pass.

## Phase 3 — refresh lifecycle

Single daemon ownership plus registration-level single-flight, generation checks, atomic token replacement, transient versus permanent failures and crash/persistence recovery. Refresh on one registration must not mutate another. Tests cover independent A/B refresh, rotating refresh token, concurrent refresh, expired/reused/revoked token and persist-after-rotation failure.

## Phase 4 — real quota

Account-specific verified sources with adapters. Parse actual header/event/window/limit fields; optional background polling only through an authorized verified endpoint. Third-party OAuth's access to Codex private quota is not assumed. Unknown/stale is visible. Test extra limit families, missing/malformed fields, clock/reset boundaries and independent snapshots.

## Phase 5 — sticky scheduler

Global active, eligibility, pause/resume, drain/undrain, enabled, model compatibility, cooldown and manual activation epoch. Restored standby never steals active. Tests include simultaneous requests and late completion after manual switching.

## Phase 6 — automatic quota/auth switching

Configurable window thresholds, structured exhaustion and permanent auth failure, bounded pre-commit failover and auto-switch toggle. No generic 429/network/cancel failover. All-unavailable returns a controlled error. Tests include A exhausted -> B; B exhausted with A freshly recovered -> A; no eligible account; stale quota; no retry cycles.

## Phase 7 — full workflow regression

Repeat the early gate on the final implementation: Resume, Compact, long session, Tool Call, MCP, Shell, Git, tool results, worktree continuity, prompt cache, A/B migration, stream interruption and official recovery. Use side-effect counters and scratch repositories. Codex's own reattempts are distinguished from transparent proxy replays. No claim of semantic context preservation based solely on a successful HTTP response.

## Phase 8 — localhost Dashboard

Accounts/quota/auth health, active/in-flight distinction, radio selection, pause/drain/enable controls, thresholds, requests and switch reasons. Secure browser sessions and CSRF, bounded authenticated SSE status, reconnection/full snapshot, shared revision semantics. Complex settings remain here. Test manual/automatic updates and slow/disconnected UI clients.

## Phase 9 — Windows floating window

Native backdrop spike before choosing WPF/WinUI implementation. Full, compact and capsule modes; radio selection; theme, topmost, drag, tray, hotkey, restored position, DPI/multi-monitor. Closing window leaves daemon healthy. Verify auto-switch immediately updates radios; reject invalid activation without displaying a false selected account. Validate solid fallback, RDP, mixed 100/150/200% DPI and disconnected monitors.

## Acceptance matrix

| Requirement | Fixture tests | Live evidence required |
|---|---|---|
| A/B remain signed in, independent grants | Isolated store records | Two browser authorizations |
| A refresh leaves B unchanged and vice versa | Fake issuer counters/generations | Actual rotation on both |
| Manual/automatic selection | Leases/epochs/counters | Mid-session A->B |
| Threshold / exhausted / recovered / no eligible | Synthetic snapshots | Account quota mapping |
| 401 / definite quota 429 / generic 429 | Classified error fixtures | Real rejection shapes when obtainable |
| Network/cancel/partial stream | Fault injection, attempt counts | Official client recovery |
| No replay after created/output/tool event | Downstream bytes + upstream counters | Shell/MCP/Git effect counters |
| Resume / Compact / long session | Official CLI/app-server fixture | Real retained/encrypted context |
| Tool result and conversation continuity | Input shape/call IDs and counters | Task facts survive boundary |
| Prompt cache | Preserve declared fields | Cached-token/latency observation; no guaranteed cross-account hit |
| Dashboard/native radios | Revision and reconnection tests | Native UI observation |
| Credentials never leak | Canary scans and API views | Storage/log inspection without exposing values |

## Current status

- Completed: initial parallel Luna research and primary architecture review.
- Completed: architecture and phase plan written before implementation.
- Completed: Phase 0 fixture transport, official CLI/app-server probes, DPAPI/OIDC login bootstrap and two different real SIWC registrations.
- Completed within tested scope: GPT-6 Luna same-thread A/B, local compact, resume, encrypted reasoning and a controlled MCP operation. See docs/phase0-results.md.
- Verified in limited live scope: both SIWC grants refreshed; A/B small-session continuation, compact/resume, encrypted reasoning and fresh unpredictable MCP result returned on B with side-effect count one.
- Implemented: native authorization adapter and persisted quota snapshots, sticky routing/epochs, gated automatic failover, secure browser Dashboard/SSE and native Windows Acrylic/tray client. Per-phase implementation commits are separate; these do not clear the live release gates.
- Synthetic verification: 25 transport/scheduler cases and 15 credential/local-API cases; official CLI fixture has seven passing scenarios. Actual Windows radio A->B updated active state.
- Native live verification: two different Plus users, real 5h/weekly windows, two token rotations per account with credential isolation, eight-stage continuation/compact/resume/reasoning/MCP regression, and real quota thresholds automatically switching A->B->A. See docs/native-acceptance-results.md.
- Pending: natural exhaustion/reset and live failure matrix, remote encrypted compaction/long sessions, Shell/Git execution counters, prompt cache and Desktop acceptance. Dashboard visual inspection stopped when Computer Use could not confidently identify the browser URL. Native modes/tray/hotkey/mixed-DPI/disconnected-monitor behavior need full UI acceptance.

Do not check off pending live requirements using synthetic accounts, a README assertion or source inspection alone.

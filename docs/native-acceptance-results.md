# Native account acceptance results

Recorded 2026-10-13, Asia/Shanghai. Installed official Codex CLI/app-server: `0.157.1`; real model: GPT-6 Luna. Evidence remains only in the primary checkout's ignored `.research/`. This document includes no credentials or real user identifiers.

## Passed in the tested scope

- Two different ChatGPT users completed official native authorization in separate temporary profiles. Both report Plus and remain ready. The helper profile count after cleanup is zero. Existing SIWC registrations remain independently stored.
- Both accounts returned genuine usage windows: duration 18,000 seconds identifies 5h; 604,800 identifies weekly. Initial samples were A 0%/94% used and B 61%/25% used. These are historical samples, not permanent values. Snapshots are saved per registration in DPAPI and refreshed independently.
- Each native account refreshed twice. All four operations rotated the refresh token, changed the access token and confirmed other accounts' access/refresh credentials were unchanged. The daemon alone owns refresh; no second refreshable helper copy remains.
- Native HTTP/SSE regression passed all eight stages: initial inference, same-thread A->B, official compact on B, return to A, official restart/resume on B, reasoning on A and continuation on B, MCP tool on A with its result continued on B.
- The MCP result contains a fresh unpredictable proof absent from the prompt. B echoed it, and the side-effect counter is one. Responses Lite's encrypted representations are not adequately measured by plain `function_call_output` counts.
- That run recorded ten model requests with one attempt each. Five requests carried encrypted input items. All returned successful completion; nine clients disconnected after a delivered `response.completed`, and one stream ended normally.
- A separate eight-stage experiment used actual quota windows and placed configured thresholds between their values. The same official thread automatically moved A->B on weekly threshold, then B->A on 5h threshold, preserving controlled facts. Ten requests each had one attempt, and the MCP counter remained one. Original thresholds and auto-switch OFF were restored afterwards.

The first native arithmetic assertion was a test false negative: the correct result used a thousands separator (`2,770`). The test now normalizes valid thousands grouping for this numeric fact, without changing marker or unpredictable proof assertions. The subsequent full run passed.

## Implementation checks

25 transport/scheduler tests and 15 credential/local-API tests pass. These cover no replay after committed body, unknown/network/cancel failures, structured rejection, fixed per-attempt backend account routing, durable selection failure, epochs, native JSON refresh, optional replacement refresh token, account isolation, DPAPI, OIDC, browser sessions/CSRF and initial status SSE. Official CLI fixture has seven passing scenarios.

The native floating window was visibly rendered and its Radio A->B command changed daemon state and synchronized the window during earlier manual UI verification. The full UI matrix is not yet accepted.

## Still pending

- Natural quota exhaustion/reset and real 401/429 failure recovery. Changing a configured threshold is not evidence of quota replenishment.
- Refresh ambiguity/crash after real remote rotation, beyond the synthetic durable `refresh_pending` tests.
- Remote encrypted compaction, larger/longer sessions, prompt-cache measurements and Desktop provider acceptance. The verified custom-provider compact path is not a general guarantee for remote encrypted state.
- Actual Shell/Git execution counters. The Windows fixture saw a CreateProcess rejection on a harmless read; its precise cause is unresolved. A rejected command is not an executed side effect.
- Dashboard visual acceptance and native automatic Radio updates, modes, tray/hotkey, monitor disconnects and mixed DPI. Windows Computer Use stopped because it could not identify the current browser URL confidently; its blocked UI operation was not retried through another input mechanism.

Production auto-switch remains gated and defaults OFF. Native/SIWC profiles are kept separate for automatic selection and same-request failover. No Codex compatibility patch is currently required by the successful tested paths. Codex still owns its tools, session, context, compaction, resume and recovery.

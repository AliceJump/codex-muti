# Phase 0 verification results

Recorded 2026-10-13, Asia/Shanghai. Real acceptance used installed official Codex CLI/app-server `0.157.1` and GPT-6 Luna. Source research was pinned separately to `6c372fbef45f730b6d8aa727b0ec8332365588db`; the installed version's source tag is `366504394c5b38c2990ccf2a3457165ca3e9d9726`.

## Evidence scope

Two different users completed independent official SIWC dynamic-registration OAuth flows. Credentials are in Windows DPAPI CurrentUser storage outside the repository. Neither the user's Codex auth.json nor its session/worktree files were changed. Each account's public model catalog included GPT-6 Luna.

The real test observed successful inference in one official thread on A, same-thread continuation on B, official local compaction on B, return to A, official process restart/resume and continuation on B. A medium-effort reasoning response was then continued on B with encrypted input items still present. A configured MCP counter ran once; its fixture selected B before returning the tool result and the official thread completed. The proxy did not execute the MCP tool.

Evidence files remain in the primary checkout's ignored `.research/` directory. They are intentionally excluded from Git. Exact transcripts, OAuth secrets and real account identities are not copied into this document.

## Verified

- Official CLI can use a localhost custom Responses provider without modification.
- Independent A/B browser authorizations and model catalogs succeed.
- Small same-thread HTTP/SSE A->B continuation preserves controlled test facts.
- Official local compact, return to another account and resume preserve these facts.
- At least one cross-account request carries encrypted reasoning input; a successful response alone does not prove every encrypted state format is portable.
- MCP side effect counter equals one and the controlled task completes after fixture activation of B.
- Synthetic error tests prohibit transparent replay after downstream commitment; ordinary 429, 5xx, network ambiguity and cancellation do not authorize failover.
- Official-client mock tests cover plain response, definite quota failover, tool rejection/result return, MCP count, resume, compact and partial-stream failure without calling B.
- Security tests cover DPAPI plaintext canaries, OIDC signature/issuer/audience/nonce/lifetime validation, one-use callback state, PKCE, registration isolation, synthetic rotation/single-flight, no-account API, Host/Origin control and single daemon ownership.

## Compatibility learned from the real route

The authorized public endpoint returned valid SSE without a Content-Type header. Its first response.created event can exceed 8 KiB. The transport now accepts bounded typed SSE framing when that header is missing and forwards every sampled byte unchanged. Codex validates complete SSE JSON events; the proxy does not reconstruct them or manufacture completion. Explicit non-SSE content types are rejected.

GPT-6 Luna's Codex request uses Responses Lite with `additional_tools`. Preserve `x-openai-internal-codex-responses-lite` as well as the body. Plain function_call_output counting alone does not fully observe Lite/encrypted tool representations.

## Still pending — not release guarantees

- Real refresh-token rotation on both grants, transient/ambiguous refresh recovery and crash-after-rotation persistence.
- A verified machine-readable Codex 5h/weekly quota source compatible with the chosen authorization route. SIWC docs do not establish one; no unknown value is shown as 0%.
- Remote encrypted compaction, WS continuation, large/long sessions and comprehensive adversarial failure cases.
- Shell/Git side-effect counters: current official CLI test policy blocked even harmless shell commands. Rejection/result round-trip passed; actual command execution did not.
- Desktop custom-provider compatibility, approval/MCP variations, prompt-cache metrics and worktree continuity under live failures.
- Automatic sticky scheduling, Dashboard and Windows Acrylic/tray UI.

Production automatic switching remains disabled. `--live-boundary-probe` only permits explicit manual migration experiments; successful small-session tests do not clear all release gates. Codex remains the owner of workflow recovery and tool execution.

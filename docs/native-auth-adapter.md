# Native Codex authorization adapter

The daemon supports two separate grant profiles. SIWC registrations use the documented public Responses route. Native Codex registrations use the installed official CLI's login flow and the source-confirmed Codex backend route. A SIWC token is never sent to a private Codex endpoint.

`native-login <name>` launches official `codex login` with a fresh, isolated `CODEX_HOME` and file credential storage in the daemon's CurrentUser-only data directory. It never reads or changes the user's default Codex auth.json. After the helper exits, the daemon validates the ID-token signature, audience and account identity, deletes the helper's auth.json, then saves one DPAPI-protected credential generation. Only the daemon refreshes that generation. A crash between deletion and persistence requires a new login; it does not leave two refresh owners.

This is an authorization adapter, not a session or agent adapter. Native usage is read from the endpoint and fields used by the pinned official source (`chatgpt.com/backend-api/wham/usage`). Window duration identifies 5h and weekly windows; missing values remain unknown. Cached per-account snapshots survive restart and become visibly stale. This private interface is version-sensitive and is not a public API guarantee.

For ChatGPT Desktop composer unblocking when routing through the local pool, see [wham-usage-rewrite.md](wham-usage-rewrite.md).

Native A/B login, two refresh rotations on each account and real quota mapping passed on 2026-10-13. Native continuation, compact/resume, reasoning, MCP proof retention and configured threshold switching also passed in small-session tests. See [native-acceptance-results.md](native-acceptance-results.md) for the scope and remaining gates. Automatic migration remains opt-in under the explicit live-boundary probe gate until the broader live matrix passes.

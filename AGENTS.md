# Codex Pool repository rules

- 用户默认使用中文。面向用户的回复、说明、验收报告和其他交付文档默认使用中文；代码标识符、协议字段及必要的技术名称保留原文。只有用户明确要求时才改用其他语言。
- Codex owns the workflow. This repository only owns model access, accounts, authorization, quota and UI clients.
- Never implement an agent loop, execute model tools, manipulate Codex conversations/worktrees or rotate Codex auth.json.
- User-authorized exception: the experimental client bridge may request official compaction at an idle boundary while owning the sole App Server stdio pipe. It must defer new client workflow input, exclude autonomous goals/additional connections, and forward real lifecycle events. No history rewriting, tool execution, workflow recovery or auth changes are permitted.
- Delegate bounded evidence collection, documentation/source searches and test-result collection to GPT-6 Luna. The primary agent owns architecture, critical code, debugging and review.
- Independent research, downloaded sources and experimental transcripts belong only in the primary checkout's ignored `.research/` directory. Never include them in commits or other worktrees.
- Never log credentials, authorization/cookies, OAuth query strings, full prompts, tool arguments/results or upstream error bodies.
- Once any downstream response event/body is committed, never replay that request to another account. Unknown/network/cancel errors do not authorize failover.
- Real multi-account continuation, compaction and refresh acceptance require real-account evidence. Mock tests cannot mark those gates passed.
- Commit each completed phase separately. PRs use squash merge unless explicitly requested otherwise.

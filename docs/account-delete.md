# 账户删除行为

## 设计选择

删除当前活跃账户时：**自动挑选待命账户**。

规则（按顺序）：

1. 若该账户已被模型请求预留（包括准备中的请求、尚未使用的 failover 候选和正在 streaming 的请求），拒绝删除，返回 `409 account_in_flight`。预留与删除使用同一个锁，不以界面的发送计数作为准入判断。
2. 额度查询、模型目录读取、token 刷新或指定账户重新授权尚未完成时，拒绝删除，返回 `409 account_busy`。整个上游操作及回包保存都持有使用许可。
3. 未知 `account-id`：返回 `404 account_not_found`。
4. 删除目标为当前活跃账户，且仍有其他账户：
   - 未开启真实迁移验收模式，且已预留过模型请求时，返回 `409 live_migration_gate_pending`，与普通手动激活采用相同门槛；
   - 优先激活第一个同 provider 且 **eligible**（已启用、未暂停、未 drain、auth ready、不在冷却）的待命账户；
   - 若没有 eligible 待命，拒绝删除，返回 `409 activate_another_first`（需先恢复/启用其他账户，或先手动激活后再删）。
5. 删除唯一账户：允许；Vault 和 scheduler 的 active 同时置空，推进 epoch。以后添加账户，即使自动切换关闭也可以重新开始路由。
6. 删除非活跃账户：从 Vault 永久移除凭据、控制状态与额度快照，同时清理额度内存缓存。

API：`POST /accounts/{id}/delete`（与现有 pause/activate 等控制面一致）。

永不把 access/refresh/id token 写入日志、事件或 API 响应。

失败/取消/提前返回时，使用许可也会释放。删除不会取消或重放正在执行的模型请求。目标已删除的重新授权 callback 直接拒绝，不复活旧 ID；后台遍历已删除账户时安全跳过，不终止额度循环。

## 回归验证

新增合成凭据测试通过显式 entered/release 屏障验证：refresh、额度回包、模型候选预留、catalog 使用和 OAuth callback 期间拒绝删除；完成或取消后可以删除；A 操作不修改 B 凭据；空池重新注册可路由；旧 epoch 不能抢回 active；删除不绕过迁移门。传输测试继续覆盖首次输出后绝不重放。

实际代理运行时，避免 SecurityTests 的 `Local API` 用例占用 3210：

```powershell
dotnet run --project tests/CodexPool.SecurityTests -- --skip "Local API"
dotnet run --project tests/CodexPool.Tests
```

上述是隔离的合成凭据回归测试，不构成新的真实账户迁移验收。

# 账户删除行为

## 设计选择

删除当前活跃账户时：**自动挑选待命账户**。

规则（按顺序）：

1. 若该账户存在进行中的模型请求（`inFlight > 0`），拒绝删除，返回 `409 account_in_flight`。
2. 未知 `account-id`：返回 `404 account_not_found`。
3. 删除目标为当前活跃账户，且仍有其他账户：
   - 优先激活第一个 **eligible**（已启用、未暂停、未 drain、auth ready、不在冷却）的待命账户；
   - 若没有 eligible 待命，拒绝删除，返回 `409 activate_another_first`（需先恢复/启用其他账户，或先手动激活后再删）。
4. 删除唯一账户：允许；`ActiveAccountId` 置空。
5. 删除非活跃账户：直接从 Vault 永久移除（凭据 + 控制状态 + 额度快照）。

API：`POST /accounts/{id}/delete`（与现有 pause/activate 等控制面一致）。

永不把 access/refresh/id token 写入日志、事件或 API 响应。

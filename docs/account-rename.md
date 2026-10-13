# 修改账户名称

Web 面板中每个账户卡片的操作区提供 **改名**。输入新名称并确认后，Dashboard、悬浮窗和托盘提示通过 `/events` 同步显示。取消输入不会发送请求，也不会选择该账户。

名称去掉首尾空白后需为 1–80 个字符，不能包含换行、制表符或其他控制字符。支持中文和空格，可以给暂停或正在请求中的账户改名。

本操作只修改本机显示名称：账户 ID、OAuth grant、access/refresh token、启用/暂停状态、额度、sticky active 和调度 epoch 都保持不变。Token 刷新和重复登录后仍保留改过的名称，不需要重新登录或重启服务。

## API

```http
POST /accounts/:id/rename
Content-Type: application/json

{"name":"工作账户"}
```

沿用控制 API 的本地 Bearer 鉴权或 Dashboard cookie + CSRF 校验，模型入口凭据不能调用。成功返回 `200` 与账户 ID；未知 ID 返回 `404 account_not_found`，无效名称返回 `400 invalid_account_name`。不返回凭据。

## CLI

```powershell
.\scripts\pool.ps1 rename <account-id> "工作 账户"
```

ID 来自 `status`，名称中有空格时使用引号。

## 验证

隔离合成凭据测试覆盖：改名不变更其他状态、重开 DPAPI Vault 保留名称、刷新 token 期间改名不被覆盖、重复登录保留名称、无效名称不落盘。UI 数据订阅使用账户 ID 判断切换，因此单纯改名不会触发“已切换到账户”的通知。

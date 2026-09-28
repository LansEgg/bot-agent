# 飞书 Webhook 安全与 Token 配额面板需求

> 状态：配额服务端、面板与飞书安全边界已实现并完成合成验证
> 类型：工程需求与验收文档
> 范围：飞书 Webhook 安全边界、平台出站隔离、Token 配额账本与面板自主调配
> 数据约束：本文只使用源码、合成测试和脱敏后的现象；不读取或记录真实会话正文、成员资料、生产日志和真实凭据。

## 1. 背景与问题

近期代码审查确认了两类需要进入后续工程批次的问题：

1. 飞书公开 Webhook 在认证配置不完整时存在 fail-open 路径。普通事件可能在没有有效签名或事件 token 的情况下进入入站处理链路，并触发模型调用和飞书出站回复；空白白名单还会进一步扩大接收范围。
2. Token 配额达到上限时，系统会进入“节能静默”，但管理员无法在面板查看每日账本，也不能自主调整每日租户配额。当前面板的 `maxTokens` 只控制单次模型请求的输出上限，不等于每日 Token 配额。

本需求不改变既有平台中立架构，不把飞书、QQ 或本地通道合并为同一租户。配额继续按现有 `SourceKey` 隔离；面板只提供管理员可见、可审计的运行时配置和状态。

## 2. 目标与范围

### 2.1 目标

- 使飞书普通事件在所有认证配置组合下默认 fail closed。
- 防止伪造、重放、跨平台和跨账号的飞书消息或出站请求越过边界。
- 为公开 Webhook 增加请求大小、并发和解析前保护。
- 在面板显示每个配额租户的每日 Token 总量、prompt/completion 分项、剩余量、重置日期和当前节能静默原因。
- 允许管理员在面板保存每日租户配额上限，保存后影响后续配额判定，不修改历史用量。
- 保留当前策略：达到每日配额后，普通消息进入节能静默；明确 @ 机器人的消息仍可回复。
- 用合成测试覆盖安全拒绝、配额调整、每日重置、状态展示和配置持久化。

### 2.2 不在范围内

- 本期不实现跨平台共享额度或全局总额度；配额仍按 `SourceKey` 隔离。
- 本期不提供“清零当前用量”或“强制解除节能静默”按钮。
- 本期不自动切换备用模型，不新增低额度模型路由。
- 不把 `maxTokens` 改名为每日配额，也不改变其单次请求语义。
- 不读取、迁移或展示真实聊天正文、成员隐私或未脱敏生产日志。
- 不在本需求中重写 QQ/OneBot 协议端。

## 3. 当前代码现状与影响面

### 3.1 已有配额基础设施

- [`ITenantQuotaLedger.cs`](../../src/BotAgent.Headless/Domain/Ports/ITenantQuotaLedger.cs) 已提供配额快照读取、每日上限保存、`IsEnergySaving` 和 `RecordUsage`。
- [`TenantQuotaSnapshot.cs`](../../src/BotAgent.Headless/Domain/Ops/TenantQuotaSnapshot.cs) 已定义租户、每日上限、prompt/completion 用量、总量、剩余量、重置日期和节能状态。
- [`TenantQuotaStore.cs`](../../src/BotAgent.Headless/Adapters/Persistence/TenantQuotaStore.cs) 已使用 SQLite `tenant_quotas` 表，按 `SourceKey` 记录每日用量，按 UTC 日期重置，并在保存上限时保留当前用量。
- [`ReplyPipeline.cs`](../../src/BotAgent.Headless/Services/Reply/ReplyPipeline.cs) 已在生成模型回复前检查节能状态，并在模型调用后记录本轮用量。当前规则是：普通触发在达到上限后静默，明确 @ 仍可进入模型链路。
- [`AppDatabase.cs`](../../src/BotAgent.Headless/Adapters/Persistence/AppDatabase.cs) 已包含 `daily_token_limit`、prompt/completion 用量和 `energy_saving` 字段。

每日上限服务端边界已确定为 `1..1,000,000,000`，新租户默认值为 `50,000`。

### 3.2 已实现的飞书安全边界

- 普通事件按配置选择签名或 Verification Token 策略，认证失败、材料缺失和 URL challenge token 不匹配均 fail closed。
- 公开入口先检查 `Content-Length`，对分块请求使用 1 MiB 有上限读取器，并通过 16 个并发槽位返回 429 限流；宿主停止取消会传递到读取、JSON 解析、后续处理和响应。
- Webhook 根节点及认证、header、event、message、sender 字段执行类型安全读取；格式非法统一受控拒绝，不把异常输入升级为 500。
- 签名 timestamp 使用 5 分钟窗口；`nonce` 与 `event_id` 使用内存快速路径加 SQLite `feishu_webhook_dedup` 原子登记，TTL 清理且有界，不通过整体清空破坏有效期内幂等。
- `FeishuWhitelist` 为空或空白时拒绝普通事件，同时支持原生目标和既有内部别名匹配。
- `SendAsync` 在网络请求前校验平台、账号、会话类型和目标归属；成功必须同时满足 HTTP 2xx 与业务 `code == 0`。
- `/api/platforms` 暴露配置、当前装配、实际生效和 `restartRequired` 状态；面板保存后的网关装配差异不会被报告为已生效。

剩余边界是生命周期本身仍采用“保存后重启装配”的策略，认证材料、API 地址和白名单的运行时读取遵循当前已装配实例；状态接口会明确提示需重启。

## 4. 术语和配置边界

| 名称 | 含义 | 本期面板行为 |
| --- | --- | --- |
| `maxTokens` | 单次模型请求允许生成的最大输出 Token 数 | 保持现有输入和语义，继续在模型设置区配置 |
| `DailyTokenLimit` | 一个配额租户在 UTC 自然日内允许消耗的 prompt + completion Token 总量 | 新增每日配额配置和展示 |
| `UsedPromptTokens` | 当前 UTC 日累计输入 Token 数 | 只读展示 |
| `UsedCompletionTokens` | 当前 UTC 日累计输出 Token 数 | 只读展示 |
| 剩余量 | `max(0, DailyTokenLimit - UsedPromptTokens - UsedCompletionTokens)` | 只读展示 |
| 节能静默 | 已达到每日配额后的状态 | 只读状态；本期不提供强制解除按钮 |
| 配额租户 | 现有 `SourceKey`，例如合成测试中的 `group:10001` | 面板展示时遵守现有脱敏规则；存储 key 不改写 |

每日配额和单次 `maxTokens` 必须使用不同字段、不同说明和不同验收用例。修改 `maxTokens` 不应重置每日账本；修改每日上限也不应改变单次请求 payload 的 `max_tokens`。

## 5. 功能需求

### 5.1 飞书 Webhook 认证与资源保护

**FR-SEC-001：普通事件必须认证。**

- 普通事件至少满足一种明确的有效认证策略：
  - 配置了 Encrypt Key：校验签名、timestamp 新鲜度和 nonce 重放；
  - 未配置 Encrypt Key：校验飞书事件头或事件 payload 中的 Verification Token，并拒绝缺失或不匹配的 token。
- 如果认证材料为空、格式非法或校验失败，必须返回拒绝状态，不得触发消息入站事件、模型调用、出站发送或配额消耗。
- URL challenge 必须单独校验 Verification Token；未配置 token 时不得默认接受外部 challenge，除非存在明确的本地测试开关且不会在生产配置中开启。

**FR-SEC-002：Webhook 请求先做资源保护，再做解析。**

- 在读取 body 前检查 `Content-Length`；分块请求也必须使用有上限的读取器。
- 超过明确上限时返回 `413`，不得创建完整字符串或 JSON 对象。
- 对公开 Webhook 设置并发上限；超过上限时返回可重试的限流状态。
- 请求取消必须能够传递到 body 读取、JSON 解析和后续处理。

**FR-SEC-003：签名事件不得无限重放。**

- 使用固定时间窗验证 timestamp。
- 对 nonce/event_id 使用原子登记操作；重复事件不得再次进入消息处理。
- 去重记录采用 TTL 或有界持久化清理，不得通过整体 `Clear()` 让仍在有效期内的事件重新可用。
- 需要跨进程或跨重启幂等的事件，其 event ID 必须持久化到现有 SQLite 运行数据中，或明确记录本期只保证单进程语义并补充运营限制。

**FR-SEC-004：飞书白名单默认拒绝。**

- `FeishuWhitelist` 为空或仅为空白时，普通事件不得被允许进入业务链路。
- 白名单匹配必须覆盖原生 `chat_id/open_id` 和内部别名的既有映射语义，并保持大小写和分隔符规则明确。
- 面板文案必须明确“空白 = 拒绝”，不得把空值描述为全接收。

**FR-SEC-005：飞书出站必须做上下文隔离。**

- `SendAsync` 在网络请求前校验 context 与 adapter 的 `PlatformId`、`AccountScope` 一致。
- 校验 `message.Target` 的平台、账号、会话类型和当前 adapter 一致。
- 不匹配统一返回 `context_mismatch`，不得调用飞书 API。
- 出站成功必须同时满足 HTTP 状态为 2xx 且业务 `code == 0`；否则按失败类型返回并记录受控诊断信息。

**FR-SEC-006：运行时状态必须与网关生命周期一致。**

- 面板开启飞书后，要么动态创建并登记网关，要么保存时明确标记“需重启”，不能让状态 API 报告已启用但 Webhook 仍返回 403。
- 修改飞书认证材料、白名单或 API 地址时，必须定义哪些字段立即生效，哪些字段需要重启。
- 任何密钥、Verification Token 和 Encrypt Key 都不得在面板 payload 中以明文返回；应沿用已存在的 configured/masked/source 模式。

### 5.2 Token 配额账本与运行时策略

**FR-QUOTA-001：每日配额按现有租户隔离。**

- 继续使用现有 `SourceKey` 作为配额租户标识。
- 不合并不同平台、不同账号或不同会话的额度。
- 缺省租户只作为兼容回退，不能覆盖已存在的具体 `SourceKey` 账本。

**FR-QUOTA-002：每日上限可由面板保存。**

- 面板允许管理员为选定配额租户保存 `DailyTokenLimit`。
- 只接受正整数；服务端必须再次校验并限制在 `1..1,000,000,000`，新租户默认值为 `50,000`，不能信任前端 `min/max`。
- 保存每日上限不修改 `UsedPromptTokens`、`UsedCompletionTokens`、`ResetDate` 或历史记录。
- 当新上限低于当前已用量时，状态应立即显示为达到配额并按既有节能静默规则处理，不允许通过保存动作隐式清零。
- 配置保存应写入现有 SQLite 账本或等价的持久化存储，重启后保持。

**FR-QUOTA-003：保留当前达到上限后的行为。**

- 普通消息达到上限后进入节能静默，不调用模型，不产生新的模型 Token 消耗。
- 明确 @ 机器人的消息仍可回复；该例外必须继续经过平台白名单、参与策略、模型可用性和其他既有安全闸门。
- 不能把“明确 @”实现为绕过身份校验、平台认证、白名单或并发限制。
- 每次因配额静默都写入结构化轨迹原因 `quota_energy_saving`；面板显示同一原因的用户可读文案。

**FR-QUOTA-004：每日重置语义明确。**

- 继续使用 UTC 自然日作为账本重置边界，除非后续明确批准时区配置需求。
- 首次读取或跨日读取时将 prompt/completion 用量归零并解除节能静默。
- 面板显示当前账本的 `resetDate`，并显示下一次 UTC 重置时间或明确的日期说明。

### 5.3 面板展示与操作

**FR-PANEL-001：新增 Token 配额区。**

在现有“回复节奏”或独立“Token 配额”区域新增：

- 当前选定配额租户；
- 每日配额上限；
- 已用 Token 总量；
- prompt 已用量；
- completion 已用量；
- 剩余 Token；
- 当前 UTC 账期和下次重置时间；
- 当前状态：正常或节能静默；
- 静默原因：达到每日 Token 配额；
- 保存每日上限按钮。

状态卡必须区分“单次 maxTokens”和“每日配额”，不能只显示一个含义不明的“Token 限额”。

**FR-PANEL-002：配额租户选择必须可控。**

- 面板可以从已登记的配额租户中选择查看对象。
- 列表和标签遵守现有 `enableAgentMask` 脱敏规则；面板操作仍使用未脱敏的内部 key。
- 不得通过新增接口返回消息正文、成员资料或未脱敏生产日志来支持配额展示。
- 当没有可选租户时，显示默认账本或明确的空状态，不自动把其他租户用量合并展示。

**FR-PANEL-003：保存反馈和错误处理。**

- 保存成功后刷新配额快照，显示新的上限和当前剩余量。
- 保存失败时保留用户输入，显示服务端校验错误；不得静默回退为默认值。
- 保存动作写入现有运行时设置审计台账，至少记录租户 key 的脱敏表示、旧上限、新上限、操作者会话和时间；不记录聊天正文。
- 本期不显示或接收任何 API Key、Verification Token、Encrypt Key 等秘密值。

### 5.4 API/数据契约

建议复用现有面板鉴权和 `/api/settings`，但配额快照与运行时设置分离，避免把每个租户的账本塞入全局配置对象。

建议增加只读接口：

```text
GET /api/quotas?tenant=<encoded-source-key>
```

响应只包含配额状态，不包含消息正文：

```json
{
  "tenant": "group:10001",
  "dailyTokenLimit": 50000,
  "usedPromptTokens": 1200,
  "usedCompletionTokens": 800,
  "usedTokens": 2000,
  "remainingTokens": 48000,
  "resetDate": "2026-01-01",
  "nextResetAt": "2026-01-02T00:00:00Z",
  "energySaving": false,
  "reason": null
}
```

建议增加配置接口：

```text
POST /api/quotas
```

请求体：

```json
{
  "tenant": "group:10001",
  "dailyTokenLimit": 50000
}
```

约束：

- 两个接口都必须经过现有面板认证。
- `tenant` 必须是已登记或可由现有会话/平台注册表确认的 `SourceKey`；不得接受任意未验证 key 写入账本。
- 响应不返回真实聊天内容、模型原始输出、密钥或完整未脱敏身份资料。
- 如果实现团队选择把配置并入 `/api/settings`，仍必须保持上述字段语义、鉴权、审计和租户隔离。

## 6. 实施步骤

### 阶段 A：飞书安全边界（已完成）

1. [已完成] 在 [`FeishuBotGateway.cs`](../../src/BotAgent.Headless/Adapters/Platforms/Feishu/FeishuBotGateway.cs) 集中实现普通事件 token/签名认证、timestamp 窗口、nonce/event_id 去重和失败码；去重记录落入 SQLite。
2. [已完成] 在 [`WebUiServer.cs`](../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.cs) 与 [`WebUiServer.Platforms.cs`](../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Platforms.cs) 增加 body 上限、并发限制、取消传播和公开入口审计。
3. [已完成] 将空白飞书白名单改为拒绝，并补充合成负向断言。
4. [已完成] 复用 [`QqChatSourcePlatformAdapter.cs`](../../src/BotAgent.Headless/Adapters/Platforms/QqChatSourcePlatformAdapter.cs) 的上下文匹配模式，补齐飞书出站隔离校验。
5. [已完成] 处理非 2xx 响应、重启所需生命周期状态和飞书秘密字段不回显边界。

### 阶段 B：配额服务端（已完成）

1. [已完成] 扩展 [`ITenantQuotaLedger.cs`](../../src/BotAgent.Headless/Domain/Ports/ITenantQuotaLedger.cs)，提供读取快照、保存上限和租户枚举所需的最小接口；避免让面板直接依赖 SQLite。
2. [已完成] 扩展 [`TenantQuotaStore.cs`](../../src/BotAgent.Headless/Adapters/Persistence/TenantQuotaStore.cs)，保留每日 UTC 重置、原子写入和当前用量，增加服务端范围校验。
3. [已完成] 在 [`PanelRoutes.cs`](../../src/BotAgent.Headless/Adapters/Panel/PanelRoutes.cs) 注册配额读取和保存路由，复用现有面板认证。
4. [已完成] 在面板处理器中只返回配额状态形状，不返回会话正文；租户选择复用已登记会话并保持内部 key 与显示标签分离。
5. [已完成] 增加保存审计，确保改变上限不会清零用量或修改重置日期。

### 阶段 C：面板 UI（已完成）

1. [已完成] 在 [`index.html`](../../src/BotAgent.Headless/wwwroot/index.html) 增加 Token 配额区域、租户选择、状态和保存控件。
2. [已完成] 在 [`app.js`](../../src/BotAgent.Headless/wwwroot/app.js) 增加配额读取、保存、刷新、错误和空状态处理。
3. [已完成] 用清晰文案区分“每日配额”和“单次请求 maxTokens”。
4. [已完成] 保存成功后刷新快照；服务端返回达到上限、低于已用量和跨日重置所需的状态字段。
5. [已完成] 不在前端保存或回显秘密值；不新增原始聊天数据请求。

### 阶段 D：文档与回归

1. 更新多平台计划的安全与运行时配置章节，注明本需求的实现状态。
2. 更新面板 API 说明、配置样例和测试命令。
3. 完成安全负向测试、配额账本测试、面板探针和 Release 构建。

## 7. 边界情况与风险

| 情况 | 预期行为 |
| --- | --- |
| 每日上限为空、0 或负数 | 服务端拒绝，不写入；显示校验错误 |
| 新上限低于当前已用量 | 立即进入节能静默；用量不清零；明确显示剩余 0 |
| 配额达到上限后收到普通消息 | 不调用模型，不增加用量 |
| 配额达到上限后明确 @ 机器人 | 仅在认证、白名单和身份判断通过后允许回复 |
| UTC 跨日 | 第一次读取/判定时重置用量并解除静默 |
| 服务重启 | 配额上限、用量和重置日期从 SQLite 恢复 |
| 同一租户并发保存上限 | 采用串行/乐观并发策略，最终值明确，不能损坏用量字段 |
| 未登记 tenant key 请求写配额 | 拒绝，不创建任意账本 |
| 飞书 body 超限 | 413，不解析，不触发业务链路 |
| 签名过期或 nonce 重复 | 拒绝，不触发模型或出站 |
| 面板开启飞书但网关未装配 | 状态明确标记需重启或完成动态装配，不得伪报已生效 |
| 面板鉴权失败 | 配额和安全配置接口均拒绝，不泄露状态细节 |

## 8. 验收标准

### 8.1 飞书安全验收

- [x] 无 Encrypt Key、无有效事件 token 的普通事件被拒绝。
- [x] 缺失 token、错误 token、错误签名、过期 timestamp 和重复 nonce/event_id 均被拒绝。
- [x] 正确认证的合成普通事件只处理一次。
- [x] body 超过限制返回 413，且不会创建完整业务消息对象。
- [x] 并发超过限制时返回限流结果，服务仍可处理后续合法请求。
- [x] 空白 `FeishuWhitelist` 拒绝目标；显式白名单只允许匹配目标。
- [x] 飞书 `SendAsync` 拒绝 QQ、其他账号或错误 context 的目标，且不会发 HTTP 请求。
- [x] HTTP 非 2xx 即使 body 为 `code=0` 也不报告成功。
- [x] 飞书秘密字段不出现在 `/api/settings` 明文响应中。
- [x] 面板开关和网关实际生命周期一致，或清楚提示需重启。

### 8.2 Token 配额服务端验收

- [x] 初次读取新租户返回默认每日上限和零用量。
- [x] 保存新每日上限后，重启仍能读取新值。
- [x] 保存上限不修改 prompt/completion 用量、重置日期或历史记录。
- [x] 上限低于当前用量时显示剩余 0 并进入节能静默。
- [x] 普通触发在节能静默下不调用模型；明确 @ 仍遵守所有其他闸门并可回复。
- [x] prompt 和 completion 用量分别累计，总用量和剩余量计算正确。
- [x] UTC 跨日后用量归零、静默解除、重置日期更新。
- [x] 并发记录用量不会丢失更新或写出负剩余量。
- [x] 未登记租户不能通过接口创建任意配额账本。

### 8.3 面板验收

- [x] 面板明确区分每日配额和单次 `maxTokens`。
- [x] 面板显示已用总量、prompt、completion、剩余量、重置时间和静默原因。
- [x] 面板可以选择已登记租户，并遵守现有脱敏规则。
- [x] 保存每日上限成功后立即刷新显示。
- [x] 输入非法值、服务端拒绝或网络失败时显示错误且不覆盖旧值。
- [x] 面板不展示聊天正文、成员资料、API Key、Verification Token 或 Encrypt Key。
- [x] 无配额租户时显示明确空状态，不把多个租户汇总成一个假总量。
- [x] S51 集成场景覆盖正常读取、范围拒绝、达到上限后的用量保持、配额审计会话指纹和重启持久化；跨日与并发由生产规格探针覆盖。

## 9. 验证命令

在源码修改后，至少运行：

```powershell
# 构建宿主程序
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release --nologo

# 面板静态探针
node tests/BotAgent.FrontendProbe/probe.mjs

# 飞书与配额合成场景
$env:QQCHAT_IT_ONLY = 's50,s51'
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll

# 配额账本与生产规格探针
dotnet build tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release --nologo
dotnet tests/BotAgent.ProductionSpecProbe/bin/Release/net8.0/BotAgent.ProductionSpecProbe.dll

# 架构护栏
dotnet build tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release --nologo
dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll

# 前端脚本语法
node --check src/BotAgent.Headless/wwwroot/app.js

# 检查无意外工作树改动
git diff --check
git status --porcelain=v1
```

测试必须使用 `MockFeishuServer`、`MockOpenAi` 和合成 `SourceKey`；不得连接生产平台、读取真实会话或使用真实凭据。

## 10. 交付判断

本需求完成的最低交付条件是：

1. 飞书公开 Webhook 在认证、重放、请求资源和出站上下文四个边界上全部有负向测试。
2. 管理员能够在面板查看配额租户的完整每日账本状态，并保存每日上限。
3. `maxTokens` 与每日配额的配置和展示没有语义混淆。
4. 达到配额后的“普通消息静默、明确 @ 可回复”行为有合成回归证据。
5. 运行时状态、面板状态、SQLite 账本和文档对同一字段含义一致。
6. 所有验证只使用合成数据，工作树和文档中不出现真实身份、秘密或聊天内容。

## 11. 待实施前确认项

- 每日配额服务端边界已确定：最小值 `1`、最大值 `1,000,000,000`、新租户默认值 `50,000`。
- 配额面板当前复用已登记会话作为租户选择来源，不新增任意租户枚举接口；未登记 key 的读取和写入均拒绝。
- 飞书事件 token 的具体字段来源和签名头名称必须以当前 Feishu 事件协议 DTO/测试替身为准，不能仅凭字段名猜测。
- 若选择动态装配飞书网关，需要补充资源释放、平台注册表更新和运行时状态事件的契约测试。

## 12. 当前实现记录

- 已实现 `/api/quotas?tenant=<encoded-source-key>` 和 `POST /api/quotas`，均复用现有面板认证。
- 已实现 `1..1,000,000,000` 服务端校验、SQLite 持久化、保存不清零用量、低于已用量时立即进入节能状态、下次 UTC 重置时间和配额变更审计；审计操作者保存为 `panel-session:` SHA-256 前缀，不保存原始 session、面板 token 或查询 token。
- 已实现面板租户选择、每日上限保存、用量拆分/剩余量/重置/节能状态展示，并保持 `maxTokens` 语义独立。
- 已实现飞书普通事件 token/签名 fail-closed、5 分钟 timestamp 窗口、SQLite TTL 去重、1 MiB body 上限、16 路并发闸门、空白白名单拒绝、出站上下文隔离和 HTTP 2xx + `code=0` 双重成功判定。
- Webhook 对非对象根节点、认证字段及嵌套事件字段执行受控类型校验；取消会传递到解析和业务回调；token 接口的异常 JSON 结构转换为 `feishu_token_invalid`，并释放 HTTP 响应资源。
- 已实现 `/api/platforms` 的装配/生效/需重启状态字段；当前采用保存后重启装配策略，不把未装配网关报告为已生效。
- S50 已覆盖正确握手、错误 token、body 超限、16 槽位并发限流、端到端回复、通道隔离和重复事件；生产规格探针已覆盖缺失 token、伪造/过期签名、有效签名重复 nonce/event_id、空白白名单、上下文错配和非 2xx 出站；S51 覆盖配额面板范围校验、用量保持、审计会话指纹和重启持久化。
- 本轮补充验收全部使用合成数据：ProductionSpecProbe **59/0**（根节点、嵌套字段、取消传播、token 响应异常）；S50/S51 **18/0**（Webhook 资源与通道边界、配额审计会话指纹）；全量 IntegrationHarness **776/0**。

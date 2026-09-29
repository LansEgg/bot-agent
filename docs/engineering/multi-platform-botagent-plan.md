# 多平台 BotAgent 改造计划

> 状态：**平台适配器与飞书文本纵切已实现，统一策略接线与边界回归进行中**。阶段 0～5 的全部验收标准尚未逐项满足，不能等同于全核心中立化完成。
> 目标：在保留现有 QQ 行为、数据兼容和部署方式的前提下，把 BotAgent 演进为可插拔的多聊天平台 Agent 运行时。
> 当前范围：多聊天平台 + 现有命名的中立化；首批平台已落地飞书（Feishu Bot API）文本收发与结构化降级，本地通道继续作为零依赖回归测试适配器。
> 勘察基线：Git HEAD `df5c40f`。实施增量包含平台中立模型、ConversationIdCodec、IPlatformAdapter / IPlatformMessenger / IPlatformRegistry、能力与降级矩阵、飞书适配器及面板端点。
> 承接既有 [架构优化方案](<architecture-optimization.md>) 与 [通用 Agent 平台规划](<general-agent-platform-plan.md>)：不重做其中已落地的工具治理、有限步进循环和本地入口；旧文档的历史数字不当成本轮测试结果。

## 1. 目标与范围

### 1.1 目标

1. 保留现有 QQ 私域、QQ 官方、本地测试通道的行为和会话隔离。
2. 新增平台时只实现该平台的入站解析、出站渲染、能力声明和连接生命周期；不复制回复决策、工具治理、参与状态、会话队列和脱敏逻辑。
3. 建立平台中立的消息、会话、身份、目标和媒体模型，使 Agent 核心不再直接依赖 QQ 号、群号、OneBot 段类型或 QQ 专属动作。
4. 让平台能力有明确的可用性和降级语义：不支持语音、图片、引用或编辑时，平台适配器声明能力，核心按策略降级，而不是调用后才依赖异常。
5. 保持单进程、自包含、SQLite、极简面板和现有容器部署路线，避免引入第二套运行时。
6. 通过首个 QQ 之外的真实聊天平台证明抽象不是只为 QQ 定制（再增加一个平台作为后续验证，不默认计入首期）；本地通道继续作为无外部依赖的回归测试适配器。

### 1.2 不在范围内

- 不改写现有 QQ 协议端，不替换 NapCat/OneBot。
- 不把不同平台的账号、群组、私聊上下文自动合并；默认按平台租户隔离。
- 不承诺兼容每个平台的全部原生互动能力。
- 不在第一阶段引入跨平台消息同步、跨平台身份合并、联邦群聊或统一联系人目录。
- 不新增 shell、文件、进程控制等高权限能力；平台接入必须复用现有工具目录、策略快照、审批和预算。
- 不读取或迁移生产群聊正文；数据迁移和验证只用 schema、计数、合成消息和现有测试替身。

## 2. 当前代码现状与影响分析

### 2.1 已有的可复用基础

- [IQqChatSource.cs](<../../src/BotAgent.Headless/Services/Qq/IQqChatSource.cs>) 已提供多通道的事件、连接状态、文本/图片/语音/动作发送和单消息引用查询能力。
- [ChannelRouter.cs](<../../src/BotAgent.Headless/Services/Qq/ChannelRouter.cs>) 已把多条上行合并，并根据通道和目标路由出站；`Channels` 已通过 key 前缀隔离官方、本地和私域上下文。
- `ConversationStore`、`BotConversation`、参与状态机、`ReplyPipeline`、工具目录/闸门/审批、轨迹和面板已有实现，应优先复用；本轮未实跑，不据此断言稳定性。
- `LocalChannelSource` 的源码和既有 S48 场景体现“第三个入口可以走同一条白名单、参与判断、回复和工具治理”，但它只是本地测试入口，不等于外部平台适配器。
- `CompositionRoot` 是当前唯一装配点；新增平台必须从这里登记，不能在 `BotAgentHost` 或业务模块内部自行 `new` 具体适配器。
- 集成 harness 已有 `MockProtocol`、`MockOpenAi` 和合成场景，适合扩展为平台契约测试。

### 2.2 主要架构摩擦

1. `IQqChatSource` 的名称、参数和默认语义把通道契约绑定到 QQ：`long targetId`、`QqChatMessage`、QQ 撤回/戳一戳/群成员资料、OneBot 音乐段等都不是平台中立概念。
2. `Channels` 同时负责通道名、key 编码、数字号段、防撞和展示；加入字符串型平台标识后继续堆条件分支会形成新的浅模块和串台风险。
3. `PlainSender`、`ChannelRouter`、健康检查仍存在按数字号段推断平台的路径；`ReplyPipeline` 则有官方/非官方的行为分支。这个推断对外部平台的字符串用户标识不成立。
4. 会话和成员存储字段存在 `qq_message_id`、`sender_id`、`group_id` 等 QQ 语义；直接改列名会破坏旧数据和已有面板/命令 key，需要兼容迁移。
5. 能力是“平台可以发什么”和“当前场景允许做什么”的混合结果。多平台接入后，必须先区分平台能力声明，再由现有策略决定是否允许。
6. 现有面板的通道列表、白名单和脱敏文案包含私域/官方/本地分支；如果继续复制条件，会让每个新平台都改面板路由。
7. 当前测试主要以 OneBot JSON 为入口。若只加一个平台实现而没有平台无关契约测试，核心行为可能被 QQ 专用测试掩盖。

### 2.3 源码证据与不能夸大的结论

| 已核验位置 | 本计划据此作出的判断 |
| --- | --- |
| [宿主工程](<../../src/BotAgent.Headless/BotAgent.Headless.csproj#L1-L54>) | 单个 .NET 8 可执行工程；命名空间与产物已经叫 BotAgent，面板资源嵌入程序集；不是从零建立 Agent |
| [归一化消息与协议 DTO](<../../src/BotAgent.Headless/Services/OneBot/OneBotModels.cs#L59-L121>) | 核心消息仍放在 OneBot 文件中，消息/用户/群/引用均为 long；不是单改类名即可中立化 |
| [通道编码](<../../src/BotAgent.Headless/Domain/Qq/Channels.cs#L71-L129>)、[出站解析](<../../src/BotAgent.Headless/Services/Qq/ChannelRouter.cs#L154-L213>) | 未知通道退回私域；未注册目标通道退到第一源；按消息 id 查询又当作目标号路由。新通用路径必须拒绝未知路由，不能继承这些默认值 |
| [SQLite schema](<../../src/BotAgent.Headless/Adapters/Persistence/AppDatabase.cs#L278-L361>)、[OwnMessage](<../../src/BotAgent.Headless/Domain/Conversation/OwnMessage.cs>)、[画像端口](<../../src/BotAgent.Headless/Domain/Ports/IProfileRepository.cs>) | 不仅会话 key：成员、角色、画像范围和自身消息索引也依赖数字身份，均须纳入迁移 |
| [白名单](<../../src/BotAgent.Headless/Services/Qq/WhitelistGate.cs#L109-L165>) | Local 空名单全拒绝，QQ 官方有显式用户白名单授权；不能把旧平台空名单语义统一改掉，新平台则建议默认拒绝 |
| [本地通道](<../../src/BotAgent.Headless/Services/Local/LocalChannelSource.cs#L86-L114>) | 出箱记录实际含 Text；注释“只有形状”不等于数据结构没有正文，新增平台状态接口必须使用专用脱敏 DTO |
| [面板凭据处理](<../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs#L254-L279>) | 实现允许提交官方 secret 到密钥库，注释仍称只从环境变量读取；本计划保留现有安全写入方式，不把旧注释当真实契约 |
| [harness 场景注册](<../../tests/BotAgent.IntegrationHarness/Program.cs#L109-L127>) | s36 隐私、s48 本地通道已注册；s42 官方场景未接入且注释提示挂死风险。不能宣称全量 harness 已覆盖 QQ 官方通道 |

只读勘察不是完整正确性审计。QQ 现有能力应复用，但“源码已有”不等于“本轮证明稳定”；平台之间的共享媒体、画像与治理范围需用阶段 0 的合成隔离测试重新核实。

### 2.4 架构方向

采用四层职责和单一装配点的既有方向：

- **Domain**：平台中立的消息、身份、目标、会话引用、媒体、能力和结果类型；不引用任何协议端实现。
- **Services**：接收归一化事件，执行会话串行、参与判断、模型决策、工具治理和回复编排。
- **Adapters**：QQ/OneBot、QQ 官方、外部平台、Local 测试入口、SQLite、HTTP 等具体实现。
- **Panel/Host**：只负责配置、装配、状态展示和管理操作，不直接编排平台细节。

架构判断采用“一个适配器是假设的 seam，两个适配器才是事实”的原则：先抽出当前已有的 QQ 与 Local/Official 共同契约，再接入一个真实外部平台；不为尚未验证的功能建立过度通用的抽象。

## 3. 核心契约与数据流设计

### 3.1 平台中立的核心概念

建议新增以下领域概念，名称在实施前通过代码评审最终确定：

- `PlatformId`：稳定的平台标识，如 `qq.private`、`qq.official`、`local`、待定的外部平台；不得依赖显示名称。
- `AccountScope`：稳定的账号/租户隔离域，不是可变的连接实例 id。即使首批单账号也必须显式分配；切换账号不得继承前账号的会话和权限。
- `ConversationId`：`PlatformId + AccountScope + Kind + NativeTargetId + ThreadId?` 的值对象。Kind 区分群、私聊、频道；线程是其内可选地址，不与 Kind 重复表达。原生 id 保持原始字符串与大小写，不统一转小写或 long。
- `ParticipantId`：`PlatformId + AccountScope + NativeUserId` 的值对象。显示资料另存，不参与相等性和存储 key；不默认跨平台、跨账号合并。
- `MessageRef`：`ConversationId + NativeMessageId`；查询、引用、撤回、自身消息索引和媒体刷新都使用它，不得用消息 id 推断目标平台。
- `InboundMessage`：消息 id、会话、发送者、时间、正文、媒体、引用、提及、回复窗口、原生扩展字段的受控归一化结果。
- `OutboundMessage`：文本、媒体、引用目标、回复目标和可选平台提示；不包含 OneBot JSON 或具体 HTTP 请求。
- `PlatformCapabilities`：文本、图片、语音、文件、引用、撤回事件、编辑、线程、按钮、群成员资料等能力的声明，区分“平台支持”和“当前配置启用”。
- `DeliveryResult`：成功、部分成功、降级、拒绝、暂时失败、永久失败、结果未知及逐分片平台消息回执；错误使用稳定原因码，不把原始平台报文放入核心。
- `PlatformContext`：平台、租户/账号、连接实例和配置快照；所有出站调用显式携带 context，不能靠数字 id 反推平台。

### 3.2 适配器接口

建议把当前 `IQqChatSource` 的职责拆成两个更深的接口：

1. `IPlatformAdapter`
   - 平台标识、连接状态、能力声明。
   - 入站事件流或归一化事件回调。
   - 连接启动、停止、重连和健康状态。
2. `IPlatformMessenger`
   - 根据 `ConversationId` 发送 `OutboundMessage`，接收 `PlatformContext` 与取消令牌；调用前校验两者 PlatformId/AccountScope 一致。
   - 通过 `MessageRef` 获取/刷新引用目标（平台支持时），拒绝跨会话隐式查询。
   - 统一返回 `DeliveryResult`。

平台适配器内部可以保留 QQ 专属接口，例如 QQ 动作、OneBot 消息段、群成员角色查询；这些接口不能泄漏到 Agent 核心。`IQqChatSource` 先作为兼容外观，由 QQ adapter 实现，避免一次性重写全部调用方。

不建议第一批抽象出“所有平台通用的戳一戳、音乐卡片或按钮接口”。只有在至少两个平台都具备相同语义、且核心确实需要调用时，才提升为平台中立能力；否则保留在 adapter 的可选扩展中。

### 3.3 会话 key 与存储兼容

新 key 采用可逆、不可歧义的结构化编码，示意：

```text
v=1;platform=<escaped-platform>;account=<escaped-scope>;kind=<kind>;target=<escaped-native-id>;thread=<escaped-thread-id-or-empty>
```

实际编码必须满足：稳定、大小写规则明确、长度有上限、不能把原始显示名放入 key、能从旧 QQ key 解析回原值。

迁移策略：

- 旧私域 QQ key `group:<id>` / `private:<id>` 永久保留并继续可读写。
- 官方、本地现有前缀继续兼容解析。没有账号 scope 的旧 key 只映射一个固定 legacy 账号，重连不变，换账号不继承；其他 QQ 账号、新平台及新增线程地址使用新格式/新存储，禁止挤入旧号段或共用旧权限。
- 存储层逐步把 `source_key` 的解释收口到 `ConversationIdCodec`，不在业务层拼接或拆分字符串。
- `qq_message_id` 等旧字段保留并标注平台；新增 `platform_message_id`、`native_target_id`、`platform_id` 等字段前先做 schema 评审，避免同一事实双写不一致。
- 迁移脚本只处理结构和 key，不读取正文；上线前用合成数据库验证幂等、回滚和旧面板链接。

### 3.4 数据流

```text
平台事件
  -> IPlatformAdapter 解析并校验
  -> InboundMessage / ConversationId / ParticipantId
  -> 会话登记与串行队列
  -> 白名单、参与状态、上下文和隐私策略
  -> 模型决策与既有权限边界（普通聊天 ToolGate，// 与面板仍各自授权）
  -> OutboundMessage + 平台能力裁决/降级
  -> IPlatformMessenger
  -> DeliveryResult / 轨迹 / 会话历史
```

适配器不得把未验证的原始事件直接交给模型。平台特有字段只进入受控扩展，并有大小、字段和日志策略。新增平台状态/诊断输出只显示平台、脱敏标签、状态码、原因码、耗时和计数；受控管理界面既有消息查看不在本次移除，内部 key 保持原样，编辑名称继续区分 nameRaw 与 name。

### 3.5 命名中立化：独立交付项，不做全局替换

**用户已明确要求现有各种命名中立化。** 完成标准不是“搜不到 QQ”，而是通用模块不再借用 QQ 名称，协议模块保留准确专有名词，外部旧契约继续可用。以下新名称均为拟议名称；阶段 0 冻结映射表后才能实施。

| 范围 / 当前实际名称与位置 | 建议目标 | 兼容与实施规则 |
| --- | --- | --- |
| `BotAgent` 命名空间、`BotAgent.Headless` 工程/程序集（[工程声明](<../../src/BotAgent.Headless/BotAgent.Headless.csproj#L9-L12>)） | 保持 BotAgent，不另造产品名 | 已中立，无需重复重命名；描述中的“QQ 机器人”改为通用宿主，QQ 作为适配器能力介绍 |
| [IQqChatSource](<../../src/BotAgent.Headless/Services/Qq/IQqChatSource.cs>) | `IPlatformAdapter` + `IPlatformMessenger` | 不是机械一对一改名；旧接口仅过渡 facade，待调用方迁完删除内部旧符号 |
| [IQqMessageSender](<../../src/BotAgent.Headless/Services/Ports/IQqMessageSender.cs>) | `IConversationReplySender` | 保留节奏、审批回复、分段记账职责；不与低层平台发送端口混成一个接口 |
| `QqChatMessage` / `QqRecallEvent`（[消息模型](<../../src/BotAgent.Headless/Services/OneBot/OneBotModels.cs>)） | `InboundMessage` / `MessageDeletedEvent` | 移到拟建 `Domain/Messaging`；删除/撤回语义由 adapter 映射，正文留存策略不随改名改变 |
| `MessageId`、`UserId`、`GroupId`、`IsGroup`、`ReplyToMessageId`（同上） | `MessageRef`、`ParticipantId`、`ConversationId`、`ConversationKind`、`ReplyTo` | 类型与语义一起改；仅将 long 重命名为 string 字段而不改调用链不算完成 |
| `Channels` / `ChannelRouter`（[编码](<../../src/BotAgent.Headless/Domain/Qq/Channels.cs>)、[路由](<../../src/BotAgent.Headless/Services/Qq/ChannelRouter.cs>)） | `ConversationIdCodec` + `PlatformRegistry` + `PlatformRouter` | 编码、登记和路由各收口；`private` 是旧“QQ 私域”标识，不得混同通用 `Direct` 私聊种类 |
| 现有 `Domain/Qq`、`Services/Qq` 的通用职责 | 拟建 `Domain/Messaging`、`Domain/Platforms`、`Services/Platforms` | 逐符号迁移通用部分，不整目录替换；依赖端口的层次保持不变 |
| [OneBot 模型](<../../src/BotAgent.Headless/Services/OneBot/OneBotModels.cs>)、QQ 专用 transport/动作 | 拟建 `Adapters/Platforms/QqOneBot`、`QqOfficial` | `OneBotEventMessage`、`QqPokeEvent`、QQ 原生 JSON 字段/动作保留协议名；QQ 官方名称明确为 QqOfficial 而非含糊的 Official |
| [QqPlainText](<../../src/BotAgent.Headless/Domain/Rendering/QqPlainText.cs>)、[QQ 动作目录](<../../src/BotAgent.Headless/Services/Agent/QqActionTool.cs>)、[IQqActions](<../../src/BotAgent.Headless/Domain/Ports/IQqActions.cs>) | QQ 渲染器和 QQ 扩展能力，通用层只见中立契约 | QQ 专有实现保留 Qq；纯文本公用规则经行为测试证明可共享后才提取，不能把“QQ 点赞”改成假通用动作 |
| `QQCHAT_*` 运行时环境变量（[配置入口](<../../src/BotAgent.Headless/Adapters/Persistence/BotConfig.cs>)、[路径入口](<../../src/BotAgent.Headless/Services/AppPaths.cs>)） | 通用项 `BOTAGENT_*`；协议项如 `BOTAGENT_QQ_ONEBOT_URL` | 新旧双读；通用数据路径示例 `QQCHAT_DATA_DIR` → `BOTAGENT_DATA_DIR`；保留既有 `_FILE` 密钥入口，不输出值 |
| `QuickLoginUin`、`OneBotAddress`、`OfficialEnabled` 等（[设置模型](<../../src/BotAgent.Headless/Services/AppSettings.cs>)） | 平台实例配置下的 `SelfUserId`、`Endpoint`、`Enabled` | 旧字段仅映射到固定 legacy QQ 实例，不广播到所有平台；群/私聊范围不能因中立化合并 |
| `privateChatEnabled`、`officialEnabled` 等面板 JSON（[读写处理](<../../src/BotAgent.Headless/Adapters/Panel/WebUiServer.Settings.cs>)） | 版本化的平台实例 DTO，如 `platformInstances` 集合 | 旧端点/旧 DTO 保留投影；未加载的表单不得清空平台列表；新旧字段冲突拒绝保存 |
| `qq` 工具名、QQ 动作 id、审批和审计 | 通用工具保持已中立 id；QQ 工具保留专有 id 或经版本化改为 `qq.*` | 不改 OneBot action 名；若变工具 id，策略白名单、历史展示、预算、票据一起迁。旧票据到期/失效后才切换，禁止别名绕过审批 |
| `qq_message_id`、`group_id`、`own_messages.message_id`（[schema](<../../src/BotAgent.Headless/Adapters/Persistence/AppDatabase.cs>)） | 新增 scoped string 身份字段/表，领域侧用中立名 | 老列不直接 rename/drop；新表是新平台唯一真源，旧 QQ 继续旧表，详见 §3.7 |
| 数据库物理名称 `qqchat.db`（同上）及日志/运行目录 | 新安装可选中立物理名称，现有路径保留 | 物理改名不是首批必需项；必须显式路径配置与离线搬迁，严禁因新默认值启动空库；不得移动生产目录 |
| [Compose](<../../docker-compose.yml>) 的 `qqchat` 服务、`qqchat-agent` 镜像、`qqchat-bot` 容器 | 新装模板拟采用 `botagent` / `botagent:…`；保留 QQ 适配器 profile | 新装模板与旧部署分轨，已有容器/卷/网络名不自动换；移除核心宿主对 NapCat 的强制依赖，使非 QQ 部署不拉起 NapCat |
| [环境模板](<../../.env.example>) 中的 Compose 输入、测试与文档 | 同步新规范和兼容对照 | 模板变量≠进程变量，必须逐条核对 Compose `environment` 映射；测试辅助 `QQCHAT_IT_ONLY`、`QQCHAT_BOT_DLL` 同样需要双名解析和继承清理 |
| 工作区仓库目录名 `qqchat-src` | 可选中立工作树名 `botagent-src` | 仅是本机目录，不是产品接口；涉及外部脚本路径，独立评估/用户确认后操作，不与源码命名绑定 |
| [主 README](<../../README.md>)、[宿主 README](<../../src/BotAgent.Headless/README.md>)、[英文 README](<../../README.en.md>)、[AGENTS](<../../AGENTS.md>) | 产品称 BotAgent，平台称“平台/账号实例”，私聊称“私聊” | 保留 QQ 教程专属词；历史交接记录不批量改写，注明历史术语；示例、CI、探针、注释随实际变更精准同步 |

**外部名称迁移协议（拟议，实施前确认）：**

1. 环境变量在同一来源级别内新名优先、旧名回退；值相同合并且只产生一次弃用提示。凭据、数据路径、账号、权限名单双名值冲突时拒绝启动；其他冲突给出明确原因并采用新名，日志只显示键名。不得改变“基础设施环境优先、行为已存配置优先、模型面板覆盖”的原有类别优先级。
2. 设置采用独立 schemaVersion 与唯一内部新模型。兼容版本继续输出旧接口投影；新版 DTO 写新格式，旧客户端写入仅更新映射范围，不能抹掉未识别的新平台配置。配置 schema 的切写和数据库切写一起受回滚门禁保护。
3. API 路由、JSON 字段、命令参数、工具 id、日志机器标记均视为外部契约；逐项登记消费者和兼容测试后才能弃用。不得用全局字符串替换跨越协议 DTO、SQL、测试快照和部署脚本。
4. 通用层改名要求零业务差异；行为调整（如未知路由拒绝）独立提交、独立用例。目标内部目录/符号可分批迁移；先保留必要转接层，调用方全部迁完才删旧内部接口。
5. 旧环境变量/API 别名的保留期由发布负责人确认；至少完成旧配置升级、新配置启动、冲突输入、回滚四类测试再考虑弃用。已有会话 key 不因任何命名清理而改写。

**命名中立化验收：** 核心通用符号不再以 Qq/QQChat/OneBot 命名；残留仅限 QQ adapter、显式兼容映射、协议测试和历史记录，并逐项有说明。编译、反射引用、嵌入资源、测试选择器、序列化、CI、容器映射和文档链接均经核验；禁止用放宽架构棘轮换取“改名完成”。

### 3.6 授权与发送可靠性

| 入口 | 权限来源与改造约束 |
| --- | --- |
| 普通聊天工具 | 复用 ToolGate/ToolPolicy/ApprovalFlow，按规范身份绑定当前会话；平台能力仅是上限，不能授权 |
| `//` 高权限任务 | 保留独立的显式用户、工具、工作目录、超时及既有开关。新平台默认不开放；平台管理员不能自动成为 BotAgent 管理员 |
| 面板代发/审批 | 继续走受控面板认证和服务端授权；目标必须显式指定已登记平台实例，不能靠用户传入的 native 字段路由 |

依据：[ServerToolGate](<../../src/BotAgent.Headless/Services/Tools/ServerToolGate.cs#L20-L43>) 的接线默认关，且明确不使用聊天审批；[ToolGate](<../../src/BotAgent.Headless/Domain/Permissions/ToolGate.cs#L62-L88>) 用会话键限制目标和票据。这里的“复用治理”不意味着将这些授权机制合并。新增上下文从可信连接/已验证的事件身份生成，不信任模型或任意扩展字段声称的账号、角色。账号隔离必须覆盖队列锁、预算、冷却、画像、审批、引用、媒体缓存和去重；现有跨会话共享媒体策略单独确认，新平台默认不自动继承旧 QQ 素材。

发送结果新增 `Unknown`（可能已发送），逐分片记录回执，不把超时一概当未发送。平台无幂等支持且无法确认送达时不盲目重发。支持幂等时，键绑定账号/会话/逻辑发送/分片并持久化，期限按平台协议确定；429 尊重 Retry-After 与总重试预算。入站去重必须有容量/TTL 与重启策略；不承诺 exactly-once，队列满时给出可观察的拒绝/退避而非无界积压。

### 3.7 数据扩展与回滚门禁

本节是建议的实现策略，不在本轮执行迁移。复用现有 AppDatabase 版本迁移、仓储端口及 LegacyJsonImporter，不另建一套数据库框架。

| 阶段 | 读写真源 | 可回滚范围 |
| --- | --- | --- |
| R0 原版 | 旧 QQ/官方/Local schema 与原 key | 原样，先建立合成旧库测试 |
| R1 兼容版本 | 原有 QQ 数据仍旧表；仅加新表/映射与 reader，新平台关闭 | 配置继续旧格式时可回 R0；必须验证旧程序会否忽略扩展结构，不凭加列就认定安全 |
| R2 多平台启用 | QQ 仍走旧存储；新平台写带完整 scope 的新表，只存一份正文，不双写到旧表 | 只能直接回滚到理解新 schema 的 R1 兼容版本，并关闭新平台；不得将 R0 当作无损回滚目标 |
| R3 可选统一存储 | 后续独立方案，经确认才迁旧 QQ 表；不是本期前置条件 | 另做在线/离线迁移设计，不在本计划承诺自动无损逆迁移 |

建议新增 scoped 存储：会话唯一键为完整 ConversationId；消息主键为 `(conversation_key, seq)`，原生消息引用索引为 `(conversation_key, native_message_id)`；成员身份为 ParticipantId，画像/角色另带 ConversationId 范围；own-message 索引用 MessageRef。原有 next_seq / through_seq / recalled / archived 语义不变。数据库生成的 conversation id 与外部 ConversationId 需明确命名，不能误把原表 id 当作新地址。

发布操作顺序：停新入站并排空队列 → 停写及处理后台任务 → 使用一致性 SQLite 备份（含 WAL 一致性，不仅复制主文件）及匹配的配置版本 → 在事务中扩展 schema/唯一索引并记录版本 → 合成测试和数量校验通过后开启新平台。重复运行迁移不得重复记录；中途异常应事务回滚，重启不能半迁移启动。

回滚顺序：禁用新平台并排空/取消在途发送 → 切换到已验证 R1 产物与匹配配置 → 保留新平台表但不读写其会话。若必须回 R0，只能使用已验证的迁移前一致性快照；快照后的新数据可能无法在旧程序中使用，恢复会损失后续写入，必须先获得人工确认并保留停写后的库副本。不得承诺所有阶段无损回滚。本轮不读取生产正文；未来备份由授权运维执行，不向 agent 展开内容。

## 4. 分步骤实施清单

### 阶段 0：冻结兼容基线

涉及：[architecture-optimization.md](<architecture-optimization.md>)、[ArchitectureProbe](<../../tests/BotAgent.ArchitectureProbe/>)、现有 IntegrationHarness。

- 先确认一个代表性非 QQ 平台并只读核对官方协议/测试环境；以其账号、消息 id、线程、限流约束约束接口设计，不先造完抽象再选平台。
- 记录当前 QQ 私域、官方、本地三通道行为和 key 兼容矩阵；修复未注册的官方 s42 或补等价合成测试，登记当前失败，不沿用历史“全绿”结论。
- 核心依赖继续遵守现有 ArchitectureProbe 棘轮，迁名不提高阈值；新平台默认关闭，不要求旧 QQ 默认也关。
- 增加平台契约测试的测试数据模型，但不改生产路径。
- 明确旧 key、白名单、面板筛选、引用回复、媒体降级和脱敏的不可回归项。
- 建立新平台默认关闭、无配置不装配、无凭据不启动的安全基线。

验收：现有 build、ArchitectureProbe、SafetyProbe、ParticipationProbe、FrontendProbe 和集成 harness 全部通过。

### 阶段 N：命名基线与兼容入口（独立阶段，贯穿后续批次）

涉及 §3.5 命名矩阵所列源文件，以及配置、面板、测试、部署示例和文档。

- N0：按“通用内部符号 / QQ 专有协议 / 外部稳定契约 / 历史记录”分类，建立旧名→新名→兼容方式→调用方→测试用例登记表，不扫描私有数据目录。
- N1：先迁纯内部通用类型/命名空间及调用方，协议 DTO 留 adapter；内部改名与行为变更分批提交，保留反射和编译测试。
- N2：先实现新旧配置双读、冲突检测和旧 DTO 投影，再同步环境模板和 Compose 映射；不得先改示例造成当前程序不识别。
- N3：随阶段 1/2 更换消息身份与端口的通用命名；阶段 5 同步面板、英文文档、测试名和新装部署模板。物理库/卷/已部署服务改名另需确认，不在此阶段自动执行。
- N4：删去已无调用者的旧内部 facade；外部别名按兼容期限保留，公开快照隐私检查不可绕过。

完成标准：旧环境变量和旧客户端可用，新规范可用；敏感冲突拒绝，其他冲突按 §3.5 的优先级确定性处理；改名残留有协议/兼容理由。用户要求的命名中立化是必做，不是可选美化。

### 阶段 1：收口标识与会话编码

涉及：[Channels.cs](<../../src/BotAgent.Headless/Domain/Qq/Channels.cs>)、`Domain/Conversation/*`、[ConversationRegistry.cs](<../../src/BotAgent.Headless/Services/Conversations/ConversationRegistry.cs>)、[PlainSender.cs](<../../src/BotAgent.Headless/Services/Reply/PlainSender.cs>)、持久化会话 adapter。

- 引入平台中立的 `ConversationId`/`ParticipantId`/`PlatformContext`。
- 以 codec 集中处理新格式和旧 QQ key 兼容解析。
- 将 `Channels.IsAliasId/IsLocalId` 的出站推断逐步替换为显式 context 登记。
- 先改读路径和内部传递，再改写路径；每一步保留旧 key 的读写。
- 添加合成 key 的碰撞、非法字符、长度、大小写和回滚测试。

完成标准：不再需要通过目标数字号段判断外部平台；QQ 旧 key 的面板和 `//` 命令行为不变。

### 阶段 2：建立平台适配器 seam

涉及：新增 `Domain/Platforms/*`、`Domain/Ports/*`；[IQqChatSource.cs](<../../src/BotAgent.Headless/Services/Qq/IQqChatSource.cs>)、[ChannelRouter.cs](<../../src/BotAgent.Headless/Services/Qq/ChannelRouter.cs>)、[CompositionRoot.cs](<../../src/BotAgent.Headless/Host/CompositionRoot.cs>)。

- 定义 `IPlatformAdapter`、`IPlatformMessenger`、`PlatformCapabilities`、`DeliveryResult`。
- 为 OneBot、QQ 官方、本地通道增加 adapter 外观，内部暂时复用现有实现。
- 让 `ChannelRouter` 改为按 `PlatformId`/`ConversationId` 路由，不增加新的平台 if/else。
- 将平台状态、能力和错误原因纳入健康报告与轨迹。
- 让 `CompositionRoot` 从配置注册 adapter；禁用的平台不构造、不连接、不创建凭据对象。

完成标准：本批文本纵切只依赖平台中立 seam；其余 QQ 旧链路有明确迁移清单。全核心中立化在阶段 3/4 实证后验收，不在引入 facade 当天宣称完成。

### 阶段 3：平台能力与消息渲染

涉及：[ReplyPipeline.cs](<../../src/BotAgent.Headless/Services/Reply/ReplyPipeline.cs>)、[PlainSender.cs](<../../src/BotAgent.Headless/Services/Reply/PlainSender.cs>)、媒体发送相关服务、`Domain/Reply/*`、`Domain/Permissions/*`。

- 把文本分句、引用、媒体选择、发送节奏与平台渲染拆开。
- 按能力矩阵决定发送或降级：例如语音不可用时退化文本，图片不可用时只发描述，引用不可用时去掉引用而不丢正文。
- 统一记录每次降级的原因码；不把平台原始错误直接暴露给群聊。
- 工具目录保留统一工具 id；平台专属动作通过能力声明和 adapter 执行器注册，不绕过 ToolGate。
- 明确“平台不支持”与“策略禁止”是不同结果，便于审计和面板显示。

完成标准：同一合成入站事件在 Local adapter 上可验证核心决策，在 QQ adapter 上验证原有媒体和引用行为；能力缺失只影响该能力。

### 阶段 4：接入首个真实外部平台

涉及：新增 `Adapters/Platforms/<Platform>/`、配置映射、`CompositionRoot`、平台契约测试和部署说明。

平台选择标准：

- 有稳定的官方接收/发送协议和测试环境。
- 支持机器人私聊或群/频道消息，能映射到当前会话模型。
- 能使用合成账号和测试空间，不需要生产隐私数据。
- 具备明确的限流、重试、签名/令牌和 webhook/WS 生命周期。
- 至少覆盖文本、回复/引用或明确声明不支持，并能测试失败降级。

候选平台不在本计划中强行拍板。阶段 0 前先由用户确认至少一个代表平台，再只读核对其官方协议和合成测试环境，明确账号、线程、鉴权和限流约束后冻结契约。本轮未查询候选平台协议，不承诺具体功能、SDK 或验签算法。

实施内容：

- 入站验签、去重、重放保护、限流、断线重连和优雅停机。
- 平台 native id 全部按字符串处理，禁止复用 QQ 数字号段。
- 用户、群组、频道、线程和 bot 自身身份的映射。
- 文本/图片/语音/引用能力声明，unsupported 行为和错误码。
- 平台级白名单、租户隔离、密钥只从环境变量或 secrets store 读取，面板不回显。
- 合成事件到 Agent 核心的端到端测试；不把原始 webhook 写入仓库或日志。

完成标准：外部平台默认关闭时零网络连接；打开后能完成文本收发、断线恢复、重复事件去重、未授权拒绝和核心治理复用。

### 阶段 5：面板、配置与运维

涉及：`Adapters/Panel/*`、`wwwroot/*`、[AppSettings.cs](<../../src/BotAgent.Headless/Services/AppSettings.cs>)、README/部署文档。

- 通道列表改为由 adapter registry 生成，不新增平台专用分支页面。
- 展示平台在线状态、能力矩阵、最近错误原因码和脱敏后的会话标签。
- 配置分为部署密钥、平台连接配置和行为策略；平台 secret 只读环境变量/secrets store。
- 为每个平台提供启用、禁用、连接测试、回滚和健康检查；行为配置按现有快照语义生效；连接/账号/密钥配置先明确标注“需重启”，只有实现受控排空、停用和重建后才承诺热切换。
- 更新 [.env.example](<../../.env.example>)、容器说明、故障排查、隐私和数据迁移文档，只记录已实现能力。

完成标准：凭据不明文回显；新增状态和诊断 DTO 不含正文或原始个人标识。保留授权管理功能、原 key 和 nameRaw，平台筛选和显示脱敏一致，不能把掩码写回存储。

### 阶段 6：第二个非 QQ 平台与能力收敛（后续扩展，需另行确认）

- 选择第二个真实平台验证抽象是否足够深。
- 对比两个平台的接入代码：若核心仍需要平台判断，回到阶段 1/2 收口 seam；若 adapter 复制大段逻辑，把共同协议生命周期、限流或错误分类下沉为深模块。
- 只保留两个平台确实共有的抽象；平台独有功能继续留在 adapter，避免“万能消息协议”。

完成标准：第二平台接入不修改参与状态、模型提示词、ToolGate、会话串行和核心历史逻辑。

## 5. 边界情况与风险预案

| 风险 | 预案 |
| --- | --- |
| 同一用户在不同平台被错误合并 | `PlatformId + AccountScope` 永远参与所有会话、参与者及派生索引；默认不做跨平台身份合并 |
| 字符串 native id 被截断/规范化 | 原生 id 只按官方协议验证，保留大小写/前导零；禁止自行 trim、转小写或转 long |
| 平台重复投递或乱序 | adapter 维护事件去重键和时间/序列规则；核心按会话队列串行；不可排序时标记不确定而不是伪造顺序 |
| 平台不支持引用、媒体或编辑 | 能力矩阵预判，按原因码降级；核心仍记录最终 DeliveryResult |
| 平台限流导致重试风暴 | 平台实例独立退避、预算、并发与熔断；429/5xx 分类处理，送达不确定时遵守 §3.6，不盲重试 |
| webhook 伪造或重放 | 验签、时间窗、nonce/event id 去重；失败默认拒绝并只记形状 |
| 密钥泄漏到配置、轨迹或日志 | secret 只来自环境变量/secrets store；面板只显示掩码；审计扫描新增平台配置和文档 |
| 旧 QQ 会话断路 | 旧 key 永久兼容；迁移先读后写；合成旧库做回归和回滚演练 |
| 面板通道分支继续膨胀 | registry 驱动状态和能力；面板只消费平台中立 DTO |
| 新平台 adapter 反向污染核心 | ArchitectureProbe 增加依赖规则：Domain/Services 不引用具体 adapter namespace；所有装配只在 CompositionRoot |
| 平台政策或 SDK 变化 | 优先使用稳定 HTTP/WebSocket 协议；外部 SDK 封装在 adapter；锁定版本并保留契约测试 |
| 真实数据测试触发隐私风险 | 只用合成账号、合成事件和脱敏计数；不读取生产消息、画像、原始事件或数据库正文 |

## 6. 验证与回归测试策略

### 6.1 单元与契约测试

- `ConversationIdCodec`：旧 QQ key、新平台 key、非法输入、碰撞、大小写、长度和 round-trip。
- `PlatformCapabilities`：能力声明与降级决策矩阵。
- `InboundMessage` 归一化：文本、提及、引用、线程、媒体、空正文和未知字段。
- `DeliveryResult`：成功、部分成功、拒绝、限流、超时、永久失败和降级。
- 每个平台 adapter 的协议解析、验签、去重、重连和出站请求形状；输入全部为合成数据。

### 6.2 核心集成测试

扩展 [IntegrationHarness](<../../tests/BotAgent.IntegrationHarness/>)：

- Local adapter 驱动平台中立核心，验证白名单、参与状态、工具闸门、审批、会话串行和轨迹。
- QQ MockProtocol 保持现有 OneBot 场景，增加旧 key 和显式 context 回归。
- 外部平台使用本地 mock server，不连接真实平台；验证签名、重复事件、429、断线和发送失败降级。
- 两个平台，以及同平台不同账号，使用完全相同 native user/target/message id，验证会话、角色、画像、白名单、上下文、队列、预算、审批和出站均不串台。
- 测试未知平台/未注册账号/停用账号时拒绝，不退回 QQ；重启后没有入站学习记录，面板代发仍路由正确。
- 测试跨账号票据复用、伪造角色、旧 key 别名绕过目标限制；非 QQ 的 `//` 默认拒绝，平台管理员不自动具备高权限。
- 测试新旧命名均可读、双名冲突、旧客户端部分更新不清空新配置、环境 `_FILE`、模板变量到进程变量映射；禁止脱敏 name 回写覆盖 nameRaw。
- 测试迁移前后 seq/through_seq 不变、幂等升级、迁移中断事务回滚、R2→R1 停写回退；不测试生产数据库。
- 测试平台已接收但客户端超时、分片部分成功、重启重复投递、去重缓存满和队列背压；不得以重试获得重复发言。

### 6.3 必跑命令

以下依据现有工程路径和场景注册表核验，**未在本轮执行**。工作目录为 `%WORKDIR%`，需 .NET 8 SDK 和 Node.js。必须分别构建宿主与 harness；不得使用生产配置。harness 使用临时数据目录并启动独立宿主进程。

```powershell
dotnet build qqchat-src/src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw '宿主构建失败' }
dotnet build qqchat-src/tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'harness 构建失败' }
$probes = 'ArchitectureProbe','SafetyProbe','ParticipationProbe','PipelineEval','ProductionSpecProbe'
foreach ($probe in $probes) {
    dotnet build "qqchat-src/tests/BotAgent.$probe/BotAgent.$probe.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "$probe 构建失败" }
    dotnet "qqchat-src/tests/BotAgent.$probe/bin/Release/net8.0/BotAgent.$probe.dll"
    if ($LASTEXITCODE -ne 0) { throw "$probe 检查失败" }
}
node qqchat-src/tests/BotAgent.FrontendProbe/probe.mjs
if ($LASTEXITCODE -ne 0) { throw '面板探针失败' }
$oldFilter = $env:QQCHAT_IT_ONLY
$oldDll = $env:QQCHAT_BOT_DLL
try {
    $env:QQCHAT_BOT_DLL = (Resolve-Path 'qqchat-src/src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll').Path
    $env:QQCHAT_IT_ONLY = 's36,s48' # 已有：隐私、本地通道
    dotnet qqchat-src/tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
    if ($LASTEXITCODE -ne 0) { throw '定点场景失败' }
    Remove-Item Env:QQCHAT_IT_ONLY -ErrorAction SilentlyContinue
    dotnet qqchat-src/tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
    if ($LASTEXITCODE -ne 0) { throw '全量回归失败' }
} finally {
    $env:QQCHAT_IT_ONLY = $oldFilter
    $env:QQCHAT_BOT_DLL = $oldDll
}
```

检查输出中的场景标题和断言数非零；未知 tag 可能返回“0通过/0失败”。`platform-contract` 尚不存在，不能作为现成命令：新增场景先注册真实 tag，并加入零场景即失败护栏，再更新本文。s42 官方通道目前未注册；阶段 0 必须先修复超时/时序或补可终止的等价合成测试，不能用全量成功宣称官方覆盖完成。

历史失败/软线需如实登记并解释，不能放宽架构棘轮或掩盖失败；本轮不继承旧文档的通过数字。部署改动另做隔离容器验证：非 QQ 部署不依赖 NapCat、禁用平台无出网、健康检查可用。

### 6.4 验收指标

- 旧 QQ 私域/官方/本地场景无行为回归，旧会话 key 可读写。
- 新平台关闭时不实例化 adapter、不建立连接、不执行该平台后台任务；现有统一配置加载是否读取环境元数据不等同于启用平台，不为这一点改写全局配置机制。
- 新平台打开后文本链路端到端可用，重复/伪造/超时/限流事件有确定性结果。
- 核心 Services 不引用具体平台 adapter；平台差异经能力/策略数据注入，新增平台不追加平台专属决策分支。
- 新增诊断、测试产物、日志与交付文档不包含生产标识、密钥和真实聊天正文；受控面板功能与内部 key 按既有隐私契约保留。
- 首个非 QQ 平台通过通用 seam 接入，不复制核心回复流程；阶段 6 的第二个非 QQ 平台另行验收，目标为仅新增 adapter、配置和契约测试。

## 7. 已确认决策

1. **首批真实外部平台**：已确认为 **飞书 (Feishu)**。
2. **首批范围**：已确认为 **文本收发与结构化降级**（支持文本、回复引用、限流处理；不支持语音/图片时自动按稳定原因码降级为文本描述）。
3. **账号模型**：已确认为 **单实例/单 Bot 账号**，默认严格租户隔离。
4. **跨平台身份绑定**：默认不允许，保持平台作用域严格隔离。
5. **部署目标**：继续保持现有单进程与 Docker/Linux 部署。
6. **Webhook 承载**：由现有宿主直接承载（`POST /api/webhooks/feishu`）。
7. **跨平台消息同步**：不做跨平台广播或同步。

## 8. 计划后的首个实施批次（已完成）

1. 用合成数据补齐 `ConversationId`/codec 的测试和旧 QQ key 兼容矩阵。
2. 增加 adapter registry 与能力 DTO，并由 QQ/Official/Local 实现填充。
3. 把通道状态与平台能力展示（`GET /api/platforms` 及 `/metrics`）改为 registry 驱动。
4. 用 Local adapter 与 `QqChatSourcePlatformAdapter` 跑通平台中立的文本收发契约。
5. 实现飞书（Feishu）外部平台适配器及合成验证。

## 9. 实施与验收实录

### 9.1 已落地组件清单

- **平台中立领域模型**：
  - `Domain/Platforms/PlatformId.cs`（`PlatformId`、`AccountScope`、`PlatformContext`）
  - `Domain/Platforms/PlatformCapabilities.cs`（`PlatformCapabilities`、`CapabilityDegradation` 降级决策矩阵）
  - `Domain/Platforms/DeliveryResult.cs`（`DeliveryStatus`、`DeliveryResult`）
  - `Domain/Messaging/ConversationId.cs`（`ConversationId`、`ParticipantId`、`MessageRef`、`InboundMessage`、`OutboundMessage`）
  - `Domain/Messaging/ConversationIdCodec.cs`（`v=1;` 结构化编码与旧 QQ/官方/本地会话 key 双向回环兼容解析）
- **平台适配器端口与注册表**：
  - `Domain/Ports/IPlatformAdapter.cs`（`IPlatformAdapter`、`IPlatformMessenger`、`PlatformStatusSnapshot`、`IPlatformRegistry`）
  - `Services/Platforms/PlatformRegistry.cs`
  - `Adapters/Platforms/QqChatSourcePlatformAdapter.cs`
- **飞书（Feishu）平台适配器**：
  - `Adapters/Platforms/Feishu/FeishuBotGateway.cs`（挑战握手、Verification Token/`X-Lark-Signature` fail-closed 认证、5 分钟 timestamp 窗口、SQLite TTL 去重、白名单拦截、`@_user` 提及识别、租户 token 缓存、上下文隔离消息回复与出箱记录）
   - `Adapters/Persistence/AppDatabase.cs`（`feishu_webhook_dedup` 表由运行时幂等命令创建，原子登记 event_id/nonce 并按 TTL 清理）
- **面板与监控接线**：
  - `Adapters/Panel/WebUiServer.Platforms.cs`（`GET /api/platforms` 只读平台状态快照、`POST /api/webhooks/feishu` 入站回调）
  - `Adapters/Panel/WebUiServer.Metrics.cs`（`botagent_platform_adapter_connected` 指标）
  - `Host/CompositionRoot.cs`（唯一装配点按配置实例化，禁用时零网络零凭据）

- **Token 配额面板与账本**：
  - `Domain/Ports/ITenantQuotaLedger.cs` 与 `Domain/Ops/TenantQuotaPolicy.cs`（快照读取、1..1B 上限、默认 50,000）
  - `Adapters/Persistence/TenantQuotaStore.cs`（SQLite 持久化、UTC 重置、调额不清零用量）
  - `Adapters/Panel/PanelRoutes.cs` 与 `WebUiServer.Quotas.cs`（认证后的 `/api/quotas` 读写、已登记租户校验、审计）
  - `wwwroot/index.html` / `app.js`（每日配额独立面板，保持 `maxTokens` 单次语义）


### 9.2 验证结果记录

- `BotAgent.Headless` Release 构建：0 错误。
- `BotAgent.ArchitectureProbe`：**通过 92，失败 0**（架构规则与文件行数约束全部守住）。
- `BotAgent.SafetyProbe`：**通过 389，失败 0**（含平台策略、显式未知通道拒绝、ServerAgentRunner 禁止动作追加及无误报行为）。
- `BotAgent.ParticipationProbe`：**通过 48，失败 0**。
- `BotAgent.PipelineEval`：**通过 68，失败 0**。
- `BotAgent.ProductionSpecProbe`：**通过 59，失败 0**。
- `BotAgent.FrontendProbe`：**通过 271，失败 0**。
- 定点集成场景：
  - S50 (飞书平台 Webhook / 握手 / 签名 / 去重 / 限流 / 隔离)：**通过 10，失败 0**。
  - S51 (Token 配额面板 / 范围 / 校验 / 隔离 / 持久化)：**通过 8，失败 0**。
  - S36 ~ S49 (脱敏 / 密钥 / QQ动作 / 上下文 / Docker / 部署 / 审批 / 参与度 / 工具目录 / 步进 / 本地通道 / 会话管理)：**通过 209，失败 0**。
- 本轮验证重点：
  - 确认 ServerAgentRunner 公共 `RunAsync` 对禁止/受限动作输出的结构化与非结构化最终文本均如实附带“未执行的 QQ 动作”，且执行成功与正常执行失败不发生误报。
  - 修复 Channels/ChannelRouter 对显式未知平台的 fail-closed 拦截，杜绝未知上行通道被冒充为 QQ 私域。
- 仍待后续持续推进项：在后续阶段进一步将依赖 targetId 数字号段的历史旁路收拢到统一 `ConversationId` 与 `PlatformPolicy` 解析。

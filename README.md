# Bot Agent

[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Linux%20(Ubuntu%20%7C%20Debian%20%7C%20CentOS%20%7C%20Arch)%20%7C%20Docker-green.svg)](#快速开始)
[![Protocol](https://img.shields.io/badge/Protocol-OneBot%20v11%20%7C%20QQ%20Official-purple.svg)](#系统架构)
[![License](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)

Bot Agent 是面向 Linux 环境构建的无界面、高性能 QQ 机器人常驻服务。服务通过 OneBot v11 协议标准（如 [NapCat](https://github.com/NapNeko/NapCatQQ)）或 QQ 官方开放平台接入，与 OpenAI 兼容格式的大语言模型（涵盖 DeepSeek、通义千问、OpenAI、本地 Ollama 等）进行交互，实现群聊与私聊场景下的自主研判与拟人化回复，并内置轻量级 Web 管理控制台。

项目支持在标准 Linux 发行版（Ubuntu、Debian、CentOS、Arch 等）直接基于源码运行，无需强制依赖容器环境；同时保留了标准的 Docker Compose 容器化部署支持。

---

> [!CAUTION]
> ### 账号安全与风控防范警示
>
> 鉴于即时通信平台对机器人账号的严格风控策略，根据项目贡献者的实际封禁遭遇，请务必注意以下运行环境风险：
>
> 1. **避免在移动端与变动网络下运行**：切勿在安卓手机本地、Termux 终端或频繁切换的蜂窝数据基站网络下常驻运行协议端。此类环境下高频调用通信 API 极易被风控系统识别为异常客户端，导致账号面临永久封禁或高频冻结。
> 2. **推荐部署方案**：建议将服务部署在受信任的异地数据中心（云服务器 / VPS）、固定 IP 的家用小型主机或运行标准 Linux（Ubuntu / Debian 等）的独立设备上，保持网络出口纯净稳定。
> 3. **生产与调试隔离**：严禁使用个人主力生活账号或工作账号作为机器人端点，测试期间建议使用专用小号，或优先采用 [QQ 官方开放平台通道](#qq-官方开放平台可选) 以获得合规稳定的运行保障。

---

## 核心特性

### 智能研判与自然交互
- **发言时机自主决策**：模型根据上下文与设定阈值评估发言必要性，低于阈值保持静默；配合限流冷却算法，避免刷屏与机械式抢话。
- **人物画像与长效记忆**：基于发送者标识构建短期会话栈与长期用户画像，并持久化至嵌入式 SQLite 数据库。
- **多模态视觉感知**：自动下载群聊图片并交由视觉模型解析；内建图片字节缓存与针对时效性 URL 的自动重签机制。
- **QQ 原生互动适配**：
  - **戳一戳响应**：识别双向戳一戳动作，具备冷却门限与自主回戳逻辑。
  - **表情包协同**：自动收录审核表情包、提取语义标签，并在回复时按语境检索候选发送。
  - **上下文精确引用**：自动解析收到的回复引用目标，模型发送时自动关联目标原消息。
  - **音频解析与接梗**：群成员分享音乐卡片时，抓取歌词并进行波形特征分析（节拍、响度与段落），确保回复基于客观事实。
- **拟真分句节拍**：支持按标点符号切分长句并模拟打字延迟分批发送。
- **联网信息检索**：优先调用模型内建搜索能力，支持无缝降级至 SearxNG、MediaWiki 等结构化搜索源模板。

### 双通道与高可用架构
- **私域与官方通道隔离**：支持同时接入自建 OneBot 端点与 QQ 官方开放平台，两套通道的会话状态、权限白名单与角色设定完全隔离。
- **全量 SQLite 持久化**：消息归档、人物画像、密钥与配置变更统一保存在 `qqchat.db`（预写日志 WAL 模式），保障故障时数据一致性。
- **自研 Web 控制面板**：基于内置轻量级 HTTP 服务运行，提供在线会话监视、运行日志 SSE 实时推流、参数热配置以及协议端扫码登录。

---

## 快速开始

### 方式一：Linux 本地源码直接运行（推荐）

该方式无需安装 Docker 守护进程，直接通过 .NET SDK 编译和运行项目源码。

#### 1. 安装 .NET 8.0 SDK

- **Ubuntu / Debian**:
  ```bash
  sudo apt update && sudo apt install -y dotnet-sdk-8.0
  ```
- **Fedora / RHEL / CentOS**:
  ```bash
  sudo dnf install -y dotnet-sdk-8.0
  ```
- **Arch Linux**:
  ```bash
  sudo pacman -S dotnet-sdk
  ```

#### 2. 克隆与配置

```bash
git clone https://github.com/mgyanik/bot-agent.git ~/bot-agent
cd ~/bot-agent

# 生成并编辑配置文件
cp .env.example .env
vim .env
```

#### 3. 运维管理

项目包含跨 Linux 发行版兼容的控制脚本：

- **后台启动**：
  ```bash
  ./start.sh
  ```
- **前台启动（调试控制台输出）**：
  ```bash
  ./start.sh -f
  ```
- **检查运行状态与探针**：
  ```bash
  ./status.sh
  ```
- **安全停止服务**：
  ```bash
  ./stop.sh
  ```
- **查看日志输出**：
  ```bash
  tail -f runtime/logs/bot-agent.log
  ```

服务启动后，浏览器访问 `http://127.0.0.1:8080/` 即可进入 Web 管理面板。

---

### 方式二：Docker Compose 容器化运行

在具备容器环境的主机上，可采用容器编排方案：

```bash
cp .env.example .env
vim .env

# 构建并启动服务
docker compose up -d

# 查看协议端登录二维码
docker compose logs -f napcat
```

---

## 配置参考

核心运行配置位于根目录的 `.env` 文件。

### 核心参数

| 配置项 | 示例值 | 说明 |
| :--- | :--- | :--- |
| `MODEL_API_KEY` | `sk-...` | OpenAI 兼容接口的 API Key（必填） |
| `MODEL_BASE_URL` | `https://api.deepseek.com/v1` | 模型调用 Base URL |
| `MODEL_NAME` | `deepseek-chat` | 使用的模型标识 |
| `MAX_TOKENS` | `2048` | 单次回复 Token 上限 |
| `PANEL_PASSWORD` | `your_password_here` | Web 控制面板初始认证密码（至少 10 位） |
| `HEALTH_PORT` | `8080` | Web 控制面板与健康探针监听端口 |
| `WHITELIST` | `*` 或 `123456,789012` | 允许响应的 QQ 群号或用户号（逗号分隔，`*` 为全量） |
| `ONEBOT_PROTOCOL` | `ForwardWebSocket` | 协议端通信模式（`ForwardWebSocket` / `ReverseWebSocket`） |
| `ONEBOT_URL` | `ws://127.0.0.1:3001` | 协议端 WebSocket 接入地址 |
| `ONEBOT_TOKEN` | `your_token` | 协议端鉴权令牌（如设置） |
| `BOT_UIN` | `10001` | 机器人 QQ 号（留空时连接后自动获取） |

### QQ 官方开放平台（可选）

如需启用官方 Bot API：

```ini
OFFICIAL_ENABLED=1
OFFICIAL_APP_ID=your_app_id
OFFICIAL_APP_SECRET=your_app_secret
```

> [!NOTE]
> 进程完成初始化后，大多数运行时行为（如人设、回复阈值、冷却时间、表情开关等）均可在 Web 控制面板内在线动态调整并持久化，无须重启服务。

---

## Web 控制面板功能

访问 `http://<服务器IP>:8080/` 即可登录控制面板：

- **会话监视**：以对话气泡形式实时展示私聊与群聊会话流、用户画像与上下文堆栈。
- **参数热调**：在线调整 AI 互动欲望、发言阈值、冷却间隔及系统提示词。
- **模型切换**：动态修改 API Base URL、模型名称与 API Key，即时热加载生效。
- **状态观测**：查看 `/healthz`（存活状态）、`/readyz`（协议端就绪状态）与详细监控指标。
- **扫码接入**：当协议端未登录时，直接在控制面板界面渲染登录二维码。
- **实时日志**：采用 Server-Sent Events (SSE) 持续输出运行日志，首屏自动回填历史输出。

---

## 系统架构

```text
+-------------------------+            OneBot v11            +----------------------------------+
| OneBot 协议端 (NapCat)   | <---- [Forward WS / WS] ----> | BotAgent.Headless (C# / .NET 8)  |
+-------------------------+                                 |   +-- OneBot 网关与事件分发      |
                                                            |   +-- 提示词组装与研判流水线   |
+-------------------------+          官方 Open API           |   +-- 人物画像与 SQLite 持久化   |
| QQ 官方开放平台          | <----------------------------> |   +-- 内置 Web 控制面板服务      |
+-------------------------+                                 +-----------------+----------------+
                                                                              |
                                                                              v OpenAI 兼容 API
                                                            +----------------------------------+
                                                            | 大语言模型 (DeepSeek / OpenAI)   |
                                                            +----------------------------------+
```

---

## 测试与质量验证

项目包含端到端集成测试套件与安全约束探针：

```bash
# 执行自动化单元评测与架构探针
dotnet test
```

测试集覆盖白名单校验、模型研判流水线、并发竞争、表情包审核、双向引用关联等核心逻辑。

---

## 开源协议

本项目源码基于 [MIT License](LICENSE) 协议开源。使用的 OneBot 协议端（如 NapCat）请遵循其各自的代码许可与平台规范。

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S50 飞书平台（Feishu Bot API）端到端集成测试：
///   ① 平台接入声明：/api/platforms 查询已登记适配器与能力；
///   ② Webhook 握手：URL verification challenge 自动应答；
///   ③ 归一化入站与回复主链：Webhook 事件触发模型决策并向飞书 REST 接口发送回复；
///   ④ 通道隔离：飞书群聊消息不串发至 QQ OneBot 协议端；
///   ⑤ 事件幂等去重：相同 event_id 不重复触发回复。
/// </summary>
public static partial class Program
{
    private static async Task RunFeishuPlatformScenarioAsync()
    {
        Section("S50 飞书平台：端到端 Webhook 接入 · 握手挑战 · 签名去重 · 通道隔离 · 回复投递");

        const int openAiPortPreferred = 17892;
        const int botWsPortPreferred = 13112;
        const int panelPortPreferred = 18172;
        const int feishuPortPreferred = 18182;
        const string panelToken = "it-s50-token";
        const string verifyToken = "feishu_verify_token_xyz";
        const string targetChat = "oc_chat_feishu_1";

        var openAiPort = FreePort(openAiPortPreferred);
        var botWsPort = FreePort(botWsPortPreferred);
        var panelPort = FreePort(panelPortPreferred);
        var feishuPort = FreePort(feishuPortPreferred);

        var dataDir = NewDataDir("s50");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var feishuServer = new MockFeishuServer(feishuPort);
        feishuServer.Start();

        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = panelToken,
            ["QQCHAT_FEISHU"] = "1",
            ["QQCHAT_FEISHU_APP_ID"] = "cli_synthetic_appid",
            ["QQCHAT_FEISHU_APP_SECRET"] = "sec_synthetic_secret",
            ["QQCHAT_FEISHU_VERIFICATION_TOKEN"] = verifyToken,
            ["QQCHAT_FEISHU_API_BASE"] = feishuServer.BaseUrl,
            ["QQCHAT_FEISHU_WHITELIST"] = targetChat,
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        var panelUrl = $"http://127.0.0.1:{panelPort}";

        // ── ① 面板 /api/platforms 能观察到飞书平台适配器 ──
        var (platCode, platBody) = await HttpGetAsync($"{panelUrl}/api/platforms?token={panelToken}");
        var platRoot = JsonNode.Parse(platBody) as JsonObject;
        var feishuNode = platRoot?["feishu"];
        Check("★ 面板能列出已登记平台状态且飞书通道在线",
            platCode == 200 && feishuNode?["enabled"]?.GetValue<bool>() == true
            && feishuNode?["connected"]?.GetValue<bool>() == true,
            $"HTTP {platCode} {platBody}");

        // ── ② Webhook 握手 URL Verification ──
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var challengeJson = $"{{\"type\":\"url_verification\",\"token\":\"{verifyToken}\",\"challenge\":\"challenge_test_token_888\"}}";
        using var challengeContent = new StringContent(challengeJson, Encoding.UTF8, "application/json");
        using var challengeResp = await http.PostAsync($"{panelUrl}/api/webhooks/feishu", challengeContent, cts.Token);
        var challengeBody = await challengeResp.Content.ReadAsStringAsync(cts.Token);
        Check("★ 飞书 Webhook 挑战请求返回匹配的 challenge 字符串",
            challengeResp.IsSuccessStatusCode && challengeBody.Contains("challenge_test_token_888"),
            $"HTTP {(int)challengeResp.StatusCode} {challengeBody}");

        // ── ③ 飞书入站消息触发核心回复并投递至飞书 REST API ──
        openAi.ClearRequests();
        openAi.EnqueueReply("{\"suitability\": 90, \"reply\": \"你好飞书，我是BotAgent！\"}");

        var eventJson = $$"""
        {
            "token": "{{verifyToken}}",
            "header": {
                "event_id": "evt_harness_001",
                "event_type": "im.message.receive_v1"
            },
            "event": {
                "sender": {
                    "sender_id": {
                        "open_id": "ou_synthetic_feishu_user"
                    }
                },
                "message": {
                    "message_id": "om_in_001",
                    "chat_id": "{{targetChat}}",
                    "chat_type": "group",
                    "content": "{\"text\":\"@bot 飞书端到端问候\"}",
                    "mentions": [ { "key": "@_user_1", "name": "bot" } ]
                }
            }
        }
        """;

        var invalidTokenJson = eventJson.Replace($"\"token\": \"{verifyToken}\"", "\"token\": \"wrong_synthetic_token\"");
        using var invalidTokenContent = new StringContent(invalidTokenJson, Encoding.UTF8, "application/json");
        using var invalidTokenResp = await http.PostAsync($"{panelUrl}/api/webhooks/feishu", invalidTokenContent, cts.Token);
        Check("★ 错误 Verification Token 的普通事件被拒绝", (int)invalidTokenResp.StatusCode == 401,
            $"HTTP {(int)invalidTokenResp.StatusCode}");

        using var oversizedContent = new StringContent(new string('x', 1_048_577), Encoding.UTF8, "application/json");
        using var oversizedResp = await http.PostAsync($"{panelUrl}/api/webhooks/feishu", oversizedContent, cts.Token);
        Check("★ 超限 Webhook body 在解析前返回 413", (int)oversizedResp.StatusCode == 413,
            $"HTTP {(int)oversizedResp.StatusCode}");

        var overloadGate = new BlockingBodyGate(16);
        var heldRequests = Enumerable.Range(0, 16).Select(async _ =>
        {
            using var heldRequest = new HttpRequestMessage(HttpMethod.Post, $"{panelUrl}/api/webhooks/feishu")
            {
                Content = new BlockingWebhookContent(overloadGate)
            };
            return await http.SendAsync(heldRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }).ToArray();
        try
        {
            await overloadGate.AllStarted.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await Task.Delay(250, cts.Token);
            using var overloadedContent = new StringContent("{}", Encoding.UTF8, "application/json");
            using var overloadedResp = await http.PostAsync($"{panelUrl}/api/webhooks/feishu", overloadedContent, cts.Token);
            Check("★ 第 17 个并发 Webhook 请求被 16 槽位闸门限流为 429",
                (int)overloadedResp.StatusCode == 429,
                $"HTTP {(int)overloadedResp.StatusCode}");
        }
        finally
        {
            overloadGate.Release();
            var heldResponses = await Task.WhenAll(heldRequests);
            foreach (var heldResponse in heldResponses)
            {
                heldResponse.Dispose();
            }
        }

        using var eventContent = new StringContent(eventJson, Encoding.UTF8, "application/json");
        using var eventResp = await http.PostAsync($"{panelUrl}/api/webhooks/feishu", eventContent, cts.Token);
        Check("★ 飞书消息 Webhook 接收成功 (200 OK)", eventResp.IsSuccessStatusCode,
            $"HTTP {(int)eventResp.StatusCode}");

        var outboundReceived = await feishuServer.WaitForMessageAsync(1, TimeSpan.FromSeconds(30));
        Check("★ 机器人成功调用飞书 REST API 发送回复",
            outboundReceived && feishuServer.Messages.Count >= 1,
            $"飞书收到消息数: {feishuServer.Messages.Count}");

        var sentMsgBody = feishuServer.Messages.FirstOrDefault() ?? "{}";
        var sentNode = JsonNode.Parse(sentMsgBody);
        var innerContent = JsonNode.Parse(sentNode?["content"]?.GetValue<string>() ?? "{}");
        var sentText = innerContent?["text"]?.GetValue<string>() ?? string.Empty;
        var sentReceiveId = sentNode?["receive_id"]?.GetValue<string>() ?? string.Empty;
        Check("★ 回复内容与接收目标一致",
            sentText == "你好飞书，我是BotAgent！" && sentReceiveId == targetChat,
            $"text={sentText}, receive_id={sentReceiveId}");

        // ── ④ 跨通道隔离验证：QQ OneBot 协议端没有收到群消息 ──
        var leakedToQq = protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "send_group_msg"
            && MessageText(a).Contains("你好飞书"));
        Check("★ 飞书回复未泄漏至 QQ 私域协议端（严格通道隔离）", !leakedToQq,
            "飞书消息被意外发送到了 QQ OneBot 协议端");

        // ── ⑤ 重复事件去重验证 ──
        var beforeMsgCount = feishuServer.Messages.Count;
        using var dupContent = new StringContent(eventJson, Encoding.UTF8, "application/json");
        using var dupResp = await http.PostAsync($"{panelUrl}/api/webhooks/feishu", dupContent, cts.Token);
        var dupBody = await dupResp.Content.ReadAsStringAsync(cts.Token);
        Check("★ 重复投递的事件被幂等拦截，未产生重复回复",
            dupResp.IsSuccessStatusCode && dupBody.Contains("duplicate")
            && feishuServer.Messages.Count == beforeMsgCount,
            $"去重回执: {dupBody}, 消息计数: {feishuServer.Messages.Count}");

        await bot.StopAsync();
    }

    private sealed class BlockingBodyGate
    {
        private int _started;
        private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingBodyGate(int expected)
        {
            Expected = expected;
        }

        public int Expected { get; }
        public Task AllStarted => _allStarted.Task;
        public Task ReleaseTask => _release.Task;

        public void MarkStarted()
        {
            if (Interlocked.Increment(ref _started) == Expected)
            {
                _allStarted.TrySetResult();
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class BlockingWebhookContent : HttpContent
    {
        private readonly BlockingBodyGate _gate;

        public BlockingWebhookContent(BlockingBodyGate gate) => _gate = gate;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[] { (byte)'{' });
            await stream.FlushAsync();
            _gate.MarkStarted();
            await _gate.ReleaseTask;
            await stream.WriteAsync(new byte[] { (byte)'}' });
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

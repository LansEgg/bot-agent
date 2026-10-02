using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S51 面板每日 Token 配额：读取、范围校验、保存、重启持久化与租户隔离。
/// 只使用合成 SourceKey，不读取真实会话正文。
/// </summary>
public static partial class Program
{
    private static async Task RunQuotaPanelScenarioAsync()
    {
        Section("S51 面板每日 Token 配额 · 范围校验 · 持久化 · 租户隔离");

        const int openAiPortPreferred = 17893;
        const int botWsPortPreferred = 13113;
        const int panelPortPreferred = 18173;
        const int syntheticGroupId = 10001;
        const string panelToken = "it-s51-token";
        const string tenant = "group:10001";

        var openAiPort = FreePort(openAiPortPreferred);
        var botWsPort = FreePort(botWsPortPreferred);
        var panelPort = FreePort(panelPortPreferred);
        var dataDir = NewDataDir("s51");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = syntheticGroupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = panelToken,
        };

        using var bot = StartBot(env);
        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await WaitForPortAsync(panelPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await protocol.SendGroupMessageAsync(syntheticGroupId, 20002, "合成成员", "配额面板测试", 5101, ct: cts.Token);
        await Task.Delay(500, cts.Token);

        var panelUrl = $"http://127.0.0.1:{panelPort}";
        var quotaUrl = $"{panelUrl}/api/quotas?tenant={Uri.EscapeDataString(tenant)}";

        var (anonymousCode, _) = await HttpGetAsync(quotaUrl);
        Check("未认证读取配额返回 401", anonymousCode == 401, $"HTTP {anonymousCode}");

        var (unknownCode, _) = await HttpGetAsync(
            $"{panelUrl}/api/quotas?tenant={Uri.EscapeDataString("group:99999")}", panelToken);
        Check("未登记租户不能被面板任意创建", unknownCode == 404, $"HTTP {unknownCode}");

        var (initialCode, initialBody) = await HttpGetAsync(quotaUrl, panelToken);
        var initial = JsonNode.Parse(initialBody) as JsonObject;
        Check("已登记租户可读取每日配额快照",
            initialCode == 200
            && initial?["tenant"]?.GetValue<string>() == tenant
            && initial?["dailyTokenLimit"]?.GetValue<int>() == 50000
            && initial?["usedPromptTokens"]?.GetValue<int>() == 0
            && initial?["usedCompletionTokens"]?.GetValue<int>() == 0
            && initial?["remainingTokens"]?.GetValue<int>() == 50000
            && initial?["energySaving"]?.GetValue<bool>() == false
            && !string.IsNullOrWhiteSpace(initial?["resetDate"]?.GetValue<string>())
            && !string.IsNullOrWhiteSpace(initial?["nextResetAt"]?.GetValue<string>())
            && initial?["reason"] is null,
            $"HTTP {initialCode} {initialBody}");

        var (lowCode, lowBody) = await PanelPostJsonAsync(
            $"{panelUrl}/api/quotas",
            new JsonObject { ["tenant"] = tenant, ["dailyTokenLimit"] = 0 }.ToJsonString(),
            panelToken);
        Check("低于最小值的每日配额返回 400", lowCode == 400, $"HTTP {lowCode} {lowBody}");

        var (saveCode, saveBody) = await PanelPostJsonAsync(
            $"{panelUrl}/api/quotas",
            new JsonObject { ["tenant"] = tenant, ["dailyTokenLimit"] = 1000000000 }.ToJsonString(),
            panelToken);
        var saved = JsonNode.Parse(saveBody) as JsonObject;
        Check("面板可保存 1B 每日配额",
            saveCode == 200 && saved?["dailyTokenLimit"]?.GetValue<int>() == 1000000000,
            $"HTTP {saveCode} {saveBody}");

        var (afterCode, afterBody) = await HttpGetAsync(quotaUrl, panelToken);
        var after = JsonNode.Parse(afterBody) as JsonObject;
        Check("保存配额不改变已有用量字段",
            afterCode == 200
            && after?["dailyTokenLimit"]?.GetValue<int>() == 1000000000
            && after?["usedPromptTokens"]?.GetValue<int>() == initial?["usedPromptTokens"]?.GetValue<int>()
            && after?["usedCompletionTokens"]?.GetValue<int>() == initial?["usedCompletionTokens"]?.GetValue<int>(),
            $"HTTP {afterCode} {afterBody}");

        await bot.StopAsync();

        var auditActor = DbProbe.Text(dataDir,
            "SELECT actor_id FROM security_audit_log WHERE event_type = 'config_change' AND action_detail LIKE '%tenant_quota%' ORDER BY id DESC LIMIT 1");
        Check("配额保存审计记录面板会话指纹且不落原始 token",
            auditActor is not null
            && auditActor.StartsWith("panel-session:", StringComparison.Ordinal)
            && auditActor.Length == "panel-session:".Length + 16
            && !auditActor.Contains(panelToken, StringComparison.Ordinal),
            $"actor={auditActor ?? "(null)"}");

        using var bot2 = StartBot(env);
        await WaitForPortAsync(panelPort, cts.Token, bot2);
        var (restartCode, restartBody) = await HttpGetAsync(quotaUrl, panelToken);
        var restarted = JsonNode.Parse(restartBody) as JsonObject;
        Check("重启后每日配额仍然持久化",
            restartCode == 200 && restarted?["dailyTokenLimit"]?.GetValue<int>() == 1000000000,
            $"HTTP {restartCode} {restartBody}");

        await bot2.StopAsync();
    }
}

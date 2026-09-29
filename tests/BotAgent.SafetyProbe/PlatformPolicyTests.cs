using System.Threading;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Platforms;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void PlatformPolicyActionTests()
    {
        Section("多平台策略 · QQ 动作执行边界");

        var settings = new AppSettings
        {
            FeishuEnabled = true,
            PlatformPolicies =
            [
                new PlatformPolicySettings
                {
                    PlatformId = PlatformId.Feishu,
                    AccountScope = AccountScope.Default,
                    Enabled = true,
                    ChatEnabled = true,
                    AllowedActions = [],
                },
            ],
        };
        var registry = new SyntheticPlatformRegistry(new PlatformStatusSnapshot(
            PlatformId.Feishu, AccountScope.Default, "synthetic feishu", "synthetic", true, true,
            PlatformCapabilities.FeishuTextOnly));
        var resolver = new PlatformPolicyResolver(new Services.SettingsBox(settings), registry);
        var gateway = new FakeQqActions();
        var host = new SessionQqActionHost(gateway, isGroup: true, targetId: 10001,
            senderId: 20002, messageId: 30003, selfId: 10001, resolver, "feishu");
        var spec = QqActionCatalog.Find("like")!;
        var result = host.ExecuteAsync(spec, new System.Text.Json.Nodes.JsonObject
        {
            ["user_id"] = "sender",
        }, CancellationToken.None).GetAwaiter().GetResult();

        Check("★ 飞书会话拒绝 QQ 动作且不调用 OneBot 网关",
            result.Contains("仅支持私域 OneBot", StringComparison.Ordinal)
            && gateway.LikeCalls == 0, result);

        var policy = resolver.ResolveForChannel("feishu");
        Check("★ 显式空动作名单保持 fail-closed",
            policy.ActionAllowlistConfigured && !policy.CanUseAction("like"),
            $"configured={policy.ActionAllowlistConfigured}");
        var unknown = resolver.ResolveForChannel("synthetic.unknown");
        Check("★ 未注册平台拒绝文本与动作",
            !unknown.Registered && !unknown.Enabled && !unknown.CanUseAction("like"));
        var otherAccount = resolver.Resolve(new PlatformContext(PlatformId.Feishu, "synthetic-other"));
        Check("★ 不同账号不能继承已登记账号的连接状态",
            !otherAccount.Registered && !otherAccount.Connected);
        Check("★ 非法会话种类不被解析成私域目标",
            BotAgent.Domain.Qq.Channels.Parse("unknown:10001") == (false, 0L)
            && BotAgent.Domain.Qq.Channels.Parse("feishu:unknown:10001") == (false, 0L));

        // GitHub Issue #29: ConversationRegistry.GetOrCreate 对未知平台通道安全返回 null 而不抛出未捕获异常
        var conversationRegistry = new BotAgent.Services.Conversations.ConversationRegistry(
            new FakeConversationRepository(),
            new SettingsBox(settings),
            new BotAgent.Services.Local.LocalChannelSource(),
            _ => true,
            _ => { });
        var unknownChannelMsg = new BotAgent.Services.OneBot.QqChatMessage(
            0, true, 20002, 10001,
            "synthetic", "hello", DateTimeOffset.UtcNow, false,
            Channel: "unknown.platform");
        var created = conversationRegistry.GetOrCreate(unknownChannelMsg);
        Check("ConversationRegistry.GetOrCreate 对未知渠道安全降级返回 null 且不抛未捕获异常",
            created is null, created is null ? "ok" : "not null");

        // ── 测试各平台实例策略中的白名单覆盖机制 ──
        var policySettings = new AppSettings
        {
            MessageWhitelist = "10001",
            OfficialEnabled = true,
            OfficialWhitelistGroups = "8000000000000001",
            LocalChannelIds = "1",
            PlatformPolicies =
            [
                new PlatformPolicySettings
                {
                    PlatformId = PlatformId.QqPrivate,
                    AccountScope = AccountScope.Legacy,
                    GroupWhitelist = "10002",
                    PrivateWhitelist = "20003",
                },
                new PlatformPolicySettings
                {
                    PlatformId = PlatformId.QqOfficial,
                    AccountScope = AccountScope.Legacy,
                    GroupWhitelist = "8000000000000002",
                    PrivateWhitelist = "8000000000000003",
                },
                new PlatformPolicySettings
                {
                    PlatformId = PlatformId.Local,
                    AccountScope = AccountScope.Legacy,
                    GroupWhitelist = "2",
                },
            ],
        };
        var policyBox = new SettingsBox(policySettings);
        var policyGate = new BotAgent.Services.Qq.WhitelistGate(policyBox, new PlatformPolicyResolver(policyBox));

        Check("★ QQ私域实例策略白名单优先覆盖全局设置（群 10002 放行，10001 拦截）",
            policyGate.AllowsSource(isGroup: true, id: 10002) && !policyGate.AllowsSource(isGroup: true, id: 10001));
        Check("★ QQ官方实例策略白名单优先覆盖（群 8000000000000002 放行，8000000000000001 拦截）",
            policyGate.AllowsKey("official:group:8000000000000002") && !policyGate.AllowsKey("official:group:8000000000000001"));
        Check("★ 本地通道实例策略白名单优先覆盖（本地 id=2 放行，id=1 拦截）",
            policyGate.AllowsKey(BotAgent.Domain.Qq.Channels.Key(BotAgent.Domain.Qq.Channels.Local, true, BotAgent.Domain.Qq.Channels.LocalTarget(2)))
            && !policyGate.AllowsKey(BotAgent.Domain.Qq.Channels.Key(BotAgent.Domain.Qq.Channels.Local, true, BotAgent.Domain.Qq.Channels.LocalTarget(1))));
    }

    private sealed class SyntheticPlatformRegistry(PlatformStatusSnapshot snapshot) : IPlatformRegistry
    {
        public IReadOnlyList<IPlatformAdapter> Adapters => Array.Empty<IPlatformAdapter>();
        public IPlatformAdapter? GetAdapter(string platformId, string accountScope = AccountScope.Default) => null;
        public IPlatformMessenger? GetMessenger(string platformId, string accountScope = AccountScope.Default) => null;
        public IReadOnlyList<PlatformStatusSnapshot> GetSnapshots() => [snapshot];
    }
}

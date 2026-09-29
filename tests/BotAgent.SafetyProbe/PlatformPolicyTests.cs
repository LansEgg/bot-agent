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
    }

    private sealed class SyntheticPlatformRegistry(PlatformStatusSnapshot snapshot) : IPlatformRegistry
    {
        public IReadOnlyList<IPlatformAdapter> Adapters => Array.Empty<IPlatformAdapter>();
        public IPlatformAdapter? GetAdapter(string platformId, string accountScope = AccountScope.Default) => null;
        public IPlatformMessenger? GetMessenger(string platformId, string accountScope = AccountScope.Default) => null;
        public IReadOnlyList<PlatformStatusSnapshot> GetSnapshots() => [snapshot];
    }
}

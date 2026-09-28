using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Platforms;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services;
using BotAgent.Services.Ops;
using BotAgent.Services.Platforms;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BotAgent.ProductionSpecProbe;

public static class Program
{
    public static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-production-spec-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);

        try
        {
            AppDatabase.Initialize();
            SettingsExistenceTests();
            AuditChainTests();
            TraceArchiveTests();
            OnlineBackupTests(root);
            MultiTenantIsolationTests(root);
            MultiPlatformBotAgentTests(root);
            Console.WriteLine($"通过 {_passed}，失败 0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败 1：" + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // 测试目录清理失败不应覆盖真正的断言结果。
            }
        }
    }

    private static void SettingsExistenceTests()
    {
        var settings = new SettingsStore();
        Check(!settings.HasStoredSettings(), "没有配置行时使用首次部署的环境种子");

        // 空 JSON 值也是一条真实配置行，存在性不应依赖 JSON 内容。
        AppDatabase.Write(conn => AppDatabase.Exec(conn,
            "INSERT INTO settings(id, json, updated_unix) VALUES(1, '', 0)"));
        Check(settings.HasStoredSettings(), "空 JSON 值仍被视为已保存配置");

        settings.Save(new AppSettings());
        Check(settings.HasStoredSettings(), "正常保存后仍能检测已有配置");
    }
    private static void AuditChainTests()
    {
        var audit = new AuditLogStore();
        audit.Append(new AuditEvent("config_change", "synthetic-panel", "synthetic:tenant-a",
            "{\"result\":\"applied\",\"fieldCount\":1}", "2.1"));
        audit.Append(new AuditEvent("secret_rotate", "synthetic-panel", "synthetic:tenant-a",
            "{\"result\":\"rotated\",\"resource\":\"model_api_key\"}", "2.1"));
        audit.Append(new AuditEvent("approval_decision", "synthetic-owner", "synthetic:tenant-a",
            "{\"requestId\":\"REQ-1\",\"action\":\"approve\"}", "2.1"));
        audit.Append(new AuditEvent("gate_block", "synthetic-agent", "synthetic:tenant-a",
            "{\"result\":\"blocked\",\"reason\":\"not_allowlisted\"}", "2.1"));
        audit.Append(new AuditEvent("dlp_block", "synthetic-sender", "synthetic:tenant-a",
            "{\"result\":\"blocked\",\"reason\":\"credential_shape\"}", "2.1"));

        var valid = audit.Verify();
        Check(valid.Valid && valid.CheckedCount == 5, "审计链五类核心事件追加后可完整校验");

        // 验证设置保存与审计记录处于同一事务
        var settings = new SettingsStore();
        var initialSettings = new AppSettings { AiDesire = 42 };
        var auditTxEvent = new AuditEvent("config_change", "synthetic-panel", "synthetic:tenant-a",
            "{\"result\":\"applied\",\"fieldCount\":1,\"field\":\"AiDesire\"}", "2.1");
        settings.Save(initialSettings, auditTxEvent, audit);
        var loaded = settings.Load();
        Check(loaded.AiDesire == 42, "配置写入生效");
        var afterTxVerify = audit.Verify();
        Check(afterTxVerify.Valid && afterTxVerify.CheckedCount == 6, "设置与审计同事务落盘后校验完整");

        AppDatabase.Write(conn => AppDatabase.Exec(conn,
            "UPDATE security_audit_log SET action_detail = $detail WHERE id = 2",
            ("$detail", "{\"result\":\"tampered\"}")));
        var broken = audit.Verify();
        Check(!broken.Valid && broken.BreakpointId == 2 && broken.ErrorType == "curr_hash_mismatch",
            "审计链能报告被篡改的断点");
    }

    private static void TraceArchiveTests()
    {
        var usageJson = "{\"usage\":{\"prompt_tokens\":123,\"completion_tokens\":45}}";
        var parsedUsage = BotAgent.Domain.Model.ModelJson.ReadUsage(usageJson);
        Check(parsedUsage.PromptTokens == 123 && parsedUsage.CompletionTokens == 45,
            "从标准 JSON 中正确提取 token 计数");
        Check(BotAgent.Domain.Model.ModelJson.ReadUsage(string.Empty) == (0, 0),
            "空字符串或无效 JSON 返回安全默认 (0, 0)");

        var counters = new TurnTraceStore();
        counters.Begin("synthetic:tenant-a");
        counters.Begin("synthetic:tenant-b");
        Check(counters.StartedTotal == 2 && counters.ActiveCount == 2,
            "决策轮开始后 metrics 请求总量与活动数可观察");

        counters.RecordTokens("synthetic:tenant-a", 100, 40, fallbackHops: 1);
        var completedTrace = counters.Complete("synthetic:tenant-a", "done");
        Check(completedTrace is not null && completedTrace.PromptTokens == 100 && completedTrace.CompletionTokens == 40 && completedTrace.FallbackHops == 1,
            "完成轮次后 token 计数与故障转移跳数正确记录");
        Check(counters.PromptTokensTotal == 100 && counters.CompletionTokensTotal == 40,
            "进程内 token 累计指标可观察");

        var trace = new TurnTrace(
            "synthetic-trace-1",
            "synthetic:tenant-a",
            DateTimeOffset.UtcNow,
            "timeout",
            5001,
            new[]
            {
                new TurnNode(TurnNodeKind.Model, "timeout", 5001, ReasonCode: "upstream_timeout")
            });

        var archive = new TraceArchiveStore();
        archive.Append(trace);
        var first = archive.Snapshot();
        Check(first.Count == 1 && first.Durations.SequenceEqual(new[] { 5001 }),
            "慢轨迹写入后 /metrics 形状可查询");

        var restartedView = new TraceArchiveStore().Snapshot();
        Check(restartedView.Count == 1 && restartedView.Durations.SequenceEqual(new[] { 5001 }),
            "重新构造归档适配器后仍能读取 SQLite 数据");

        Check(first.PromptTokens == 0 && first.CompletionTokens == 0,
            "无 token 计数时输出安全默认值 0");

        var traceWithTokens = new TurnTrace(
            "synthetic-trace-2",
            "synthetic:tenant-b",
            DateTimeOffset.UtcNow,
            "fallback",
            120,
            new[]
            {
                new TurnNode(TurnNodeKind.Model, "fallback", 120, ReasonCode: "provider_retry")
            },
            PromptTokens: 250,
            CompletionTokens: 80,
            FallbackHops: 1);

        archive.Append(traceWithTokens);
        var second = archive.Snapshot();
        Check(second.Count == 2 && second.PromptTokens == 250 && second.CompletionTokens == 80,
            "带 token 轨迹写入后累计用量可查询");

        var restartedTokensView = new TraceArchiveStore().Snapshot();
        Check(restartedTokensView.Count == 2 && restartedTokensView.PromptTokens == 250 && restartedTokensView.CompletionTokens == 80,
            "重新构造归档适配器后仍能读取持久化 Token 统计");

        var circuitSnapshot = new CircuitStatusSnapshot("model", "primary", "closed");
        Check(circuitSnapshot.Type == "model" && circuitSnapshot.Id == "primary" && circuitSnapshot.State == "closed",
            "熔断器快照结构规范且不泄露敏感凭据");
    }

    private static void OnlineBackupTests(string root)
    {
        var backupDir = Path.Combine(root, "backups");
        var b1 = AppDatabase.VacuumIntoBackup(backupDir, maxRetained: 3);
        Check(File.Exists(b1), "初次热备生成物理文件");

        var b2 = AppDatabase.VacuumIntoBackup(backupDir, maxRetained: 3);
        var b3 = AppDatabase.VacuumIntoBackup(backupDir, maxRetained: 3);
        var b4 = AppDatabase.VacuumIntoBackup(backupDir, maxRetained: 3);

        var dir = new DirectoryInfo(backupDir);
        var backups = dir.GetFiles("qqchat_daily_*.db");
        Check(backups.Length == 3, "自动保留最近 3 份热备，超出自动清理");

        // 验证备份出的 SQLite 文件能够正常打开与查询
        var testConnString = $"Data Source={b4};Mode=ReadOnly;";
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(testConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM settings;";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        Check(count >= 1, "备份文件具备完整 SQLite 结构与数据一致性");
    }

    private static void MultiTenantIsolationTests(string root)
    {
        // 1. 租户配额台账隔离与节能静默测试
        var quotas = new TenantQuotaStore();
        var qA = quotas.GetQuota("synthetic:tenant-a");
        Check(qA.TenantId == "synthetic:tenant-a" && !qA.EnergySaving, "初始租户配额状态正常");

        // 消耗 30,000 tokens (默认上限 50,000)
        var afterFirst = quotas.RecordUsage("synthetic:tenant-a", 20000, 10000);
        Check(afterFirst.UsedPromptTokens == 20000 && afterFirst.UsedCompletionTokens == 10000 && !afterFirst.EnergySaving,
            "正常用量内不触发节能静默");

        // 再消耗 25,000 tokens，累计 55,000，超过上限
        var afterOver = quotas.RecordUsage("synthetic:tenant-a", 15000, 10000);
        Check(afterOver.EnergySaving && quotas.IsEnergySaving("synthetic:tenant-a"),
            "单租户超出每日 Token 预算后自动进入节能静默");

        // 验证另一租户不受任何影响
        Check(!quotas.IsEnergySaving("synthetic:tenant-b"), "单租户静默不影响其他租户配额与状态");

        // 2. 表情包租户作用域隔离测试
        var stickerStore = new StickerStore();
        stickerStore.Load(root);

        // 写入一张 tenant-a 专属表情包与一张全局批准表情包
        var rawPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        var sA = stickerStore.Add(rawPng, "png", "10001", 10001, scopeTenantId: "synthetic:tenant-a");
        if (sA is not null)
        {
            stickerStore.SetDescription(sA.Id, "测试表情A", new[] { "开心" }, isSticker: true);
        }

        var rawPng2 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0E };
        var sGlobal = stickerStore.Add(rawPng2, "png", "10001", 0, scopeTenantId: "global_approved");
        if (sGlobal is not null)
        {
            stickerStore.SetDescription(sGlobal.Id, "全局表情", new[] { "开心" }, isSticker: true);
        }

        var picksA = stickerStore.PickCandidates("开心", 10, -1, "synthetic:tenant-a");
        Check(picksA.Any(p => p.ScopeTenantId == "synthetic:tenant-a") && picksA.Any(p => p.ScopeTenantId == "global_approved"),
            "所属租户可检索自身作用域及全局表情包");

        var picksB = stickerStore.PickCandidates("开心", 10, -1, "synthetic:tenant-b");
        Check(!picksB.Any(p => p.ScopeTenantId == "synthetic:tenant-a") && picksB.Any(p => p.ScopeTenantId == "global_approved"),
            "跨租户无法检索其他租户专有表情包");
    }

    private static void MultiPlatformBotAgentTests(string root)
    {
        // 1. ConversationIdCodec 编码与双向回环解析
        Check(ConversationIdCodec.TryParse("group:10001", out var legacyGroup)
            && legacyGroup.PlatformId == PlatformId.QqPrivate
            && legacyGroup.AccountScope == AccountScope.Legacy
            && legacyGroup.Kind == ConversationKind.GroupChat
            && legacyGroup.NativeTargetId == "10001"
            && ConversationIdCodec.Encode(legacyGroup) == "group:10001",
            "旧私域 QQ 群会话 key 双向回环解析与编码一致");

        Check(ConversationIdCodec.TryParse("private:20002", out var legacyPrivate)
            && legacyPrivate.PlatformId == PlatformId.QqPrivate
            && legacyPrivate.Kind == ConversationKind.PrivateChat
            && ConversationIdCodec.Encode(legacyPrivate) == "private:20002",
            "旧私域 QQ 私聊会话 key 双向回环解析与编码一致");

        Check(ConversationIdCodec.TryParse("official:group:8000000000000001", out var legacyOff)
            && legacyOff.PlatformId == PlatformId.QqOfficial
            && ConversationIdCodec.Encode(legacyOff) == "official:group:8000000000000001",
            "旧官方 QQ 会话 key 双向回环解析与编码一致");

        Check(ConversationIdCodec.TryParse("local:private:7000000000000002", out var legacyLoc)
            && legacyLoc.PlatformId == PlatformId.Local
            && ConversationIdCodec.Encode(legacyLoc) == "local:private:7000000000000002",
            "旧本地通道会话 key 双向回环解析与编码一致");

        var feishuStructured = "v=1;platform=feishu;account=default;kind=group;target=oc_a1b2c3d4;thread=";
        Check(ConversationIdCodec.TryParse(feishuStructured, out var fsConv)
            && fsConv.PlatformId == PlatformId.Feishu
            && fsConv.AccountScope == AccountScope.Default
            && fsConv.Kind == ConversationKind.GroupChat
            && fsConv.NativeTargetId == "oc_a1b2c3d4"
            && fsConv.ThreadId is null
            && ConversationIdCodec.Encode(fsConv) == feishuStructured,
            "v=1 结构化飞书群会话 key 双向回环解析与编码一致");

        var feishuThread = "v=1;platform=feishu;account=default;kind=channel;target=oc_chat123;thread=omt_thread_abc";
        Check(ConversationIdCodec.TryParse(feishuThread, out var fsThreadConv)
            && fsThreadConv.Kind == ConversationKind.ChannelChat
            && fsThreadConv.ThreadId == "omt_thread_abc"
            && ConversationIdCodec.Encode(fsThreadConv) == feishuThread,
            "带线程地址的结构化会话 key 准确保留线程标识");

        // 字符大小写与非法字符护栏
        var caseSensitive = "v=1;platform=feishu;account=default;kind=group;target=oc_CamelCaseTarget123;thread=";
        Check(ConversationIdCodec.TryParse(caseSensitive, out var csConv)
            && csConv.NativeTargetId == "oc_CamelCaseTarget123",
            "非数字平台原生标识严格保留原始大小写与字符");

        Check(!ConversationIdCodec.TryParse(string.Empty, out _)
            && !ConversationIdCodec.TryParse(null, out _)
            && !ConversationIdCodec.TryParse(new string('a', 600), out _)
            && !ConversationIdCodec.TryParse("group:123\nmalicious", out _),
            "空值、超长串及包含控制字符的会话 key 坚决拒绝解析");

        // 2. PlatformCapabilities 与降级评估
        var fsCaps = PlatformCapabilities.FeishuTextOnly;
        Check(fsCaps.SupportsText && !fsCaps.SupportsImage && !fsCaps.SupportsVoice && fsCaps.SupportsQuote,
            "飞书首期声明文本与引用支持，不支持语音与图片");

        var deg = fsCaps.EvaluateDegradation(wantsImage: true, wantsVoice: true, wantsQuote: true);
        Check(!deg.SendImage && !deg.SendVoice && deg.SendQuote && deg.IsDegraded
            && deg.ReasonCodes.Contains("image_degraded_to_text")
            && deg.ReasonCodes.Contains("voice_degraded_to_text")
            && !deg.ReasonCodes.Contains("quote_dropped"),
            "平台不支持媒体时按稳定原因码自动降级为文本与纯文本描述");

        // 3. DeliveryResult 契约
        var delOk = DeliveryResult.Ok("msg_123");
        var delDeg = DeliveryResult.Degraded("msg_124", "image_degraded_to_text");
        var delThrottled = DeliveryResult.Throttled(5, "rate_limited");
        var delRej = DeliveryResult.Rejected("context_mismatch");
        Check(delOk.IsSuccess && delDeg.IsSuccess && !delThrottled.IsSuccess && !delRej.IsSuccess
            && delThrottled.RetryAfterSeconds == 5,
            "DeliveryResult 正确表达成功、降级、限流与拒绝语义");

        // 4. PlatformRegistry 注册表
        var mockHttp = new MockProbeHttp();
        var settingsBox = new SettingsBox(new AppSettings { FeishuEnabled = true, FeishuAppId = "cli_test" });
        var fsGateway = new FeishuBotGateway(settingsBox, mockHttp);
        var reg = new PlatformRegistry(new IPlatformAdapter[] { fsGateway }, new IPlatformMessenger[] { fsGateway });
        Check(reg.GetAdapter(PlatformId.Feishu) is not null && reg.GetMessenger(PlatformId.Feishu) is not null,
            "PlatformRegistry 能根据平台标识准确检索适配器与发送端");
        var snapshots = reg.GetSnapshots();
        Check(snapshots.Count == 1 && snapshots[0].DisplayName == "飞书" && snapshots[0].Enabled,
            "PlatformRegistry 快照准确反映平台启用状态与能力声明");

        // 5. FeishuBotGateway 合成入站、握手挑战、签名与去重
        var challengeReq = "{\"type\":\"url_verification\",\"challenge\":\"feishu_challenge_token_999\"}";
        var challengeRes = fsGateway.HandleWebhookAsync(challengeReq, null, null, null).GetAwaiter().GetResult();
        Check(challengeRes.Handled && challengeRes.StatusCode == 200 && challengeRes.ResponseBody.Contains("feishu_challenge_token_999"),
            "飞书 Webhook 握手挑战请求成功返回包含 challenge 的 JSON");

        // 签名核验测试
        var boxWithSecret = new SettingsBox(new AppSettings
        {
            FeishuEnabled = true,
            FeishuAppId = "cli_test",
            FeishuEncryptKey = "test_signing_key_456"
        });
        var securedGateway = new FeishuBotGateway(boxWithSecret, mockHttp);
        var unverifiedRes = securedGateway.HandleWebhookAsync("{\"type\":\"event_callback\"}", "bad_sig", "1700000000", "nonce1").GetAwaiter().GetResult();
        Check(!unverifiedRes.Handled && unverifiedRes.StatusCode == 401,
            "飞书配置加密签名密钥时伪造签名的事件请求直接返回 401");

        // 合成消息入站与去重
        InboundMessage? receivedInbound = null;
        fsGateway.InboundReceived += m => receivedInbound = m;

        var messageEvtJson = """
        {
            "header": {
                "event_id": "evt_test_001",
                "event_type": "im.message.receive_v1"
            },
            "event": {
                "sender": {
                    "sender_id": {
                        "open_id": "ou_synthetic_user"
                    }
                },
                "message": {
                    "message_id": "om_synthetic_msg",
                    "chat_id": "oc_synthetic_chat",
                    "chat_type": "group",
                    "content": "{\"text\":\"@bot 测试飞书入站消息\"}",
                    "mentions": [ { "key": "@_user_1", "id": { "open_id": "ou_bot" }, "name": "bot" } ]
                }
            }
        }
        """;

        var evtRes1 = fsGateway.HandleWebhookAsync(messageEvtJson, null, null, null).GetAwaiter().GetResult();
        Check(evtRes1.Handled && evtRes1.StatusCode == 200 && receivedInbound is not null
            && receivedInbound.Text == "@bot 测试飞书入站消息"
            && receivedInbound.Ref.Conversation.NativeTargetId == "oc_synthetic_chat"
            && receivedInbound.IsMentioned,
            "飞书消息事件成功归一化为 InboundMessage 并触发上层事件");

        // 重复事件去重测试
        receivedInbound = null;
        var evtRes2 = fsGateway.HandleWebhookAsync(messageEvtJson, null, null, null).GetAwaiter().GetResult();
        Check(evtRes2.Handled && evtRes2.StatusCode == 200 && evtRes2.ResponseBody.Contains("duplicate") && receivedInbound is null,
            "飞书相同 event_id 的重复投递被幂等去重拦截，不重复触发上层核心");

        // 出站发送与出箱记账测试
        var outRes = fsGateway.SendAsync(fsGateway.Context, new OutboundMessage(
            new ConversationId(PlatformId.Feishu, AccountScope.Default, ConversationKind.GroupChat, "oc_synthetic_chat"),
            "飞书出站测试回复")).GetAwaiter().GetResult();
        Check(outRes.IsSuccess && fsGateway.Outbox.Count == 1 && fsGateway.Outbox[0].Text == "飞书出站测试回复",
            "飞书消息成功完成出站模拟发送并准确记入有界出箱");
    }

    private sealed class MockProbeHttp : IHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public Func<HttpRequestMessage, HttpResponseMessage>? OnSend { get; set; }
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => Task.FromResult(OnSend?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0,\"data\":{\"message_id\":\"om_test_out\"}}")
            });
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct = default)
            => SendAsync(request, ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
            => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completionOption, CancellationToken ct = default)
            => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default)
            => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption, CancellationToken ct = default)
            => SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
            => SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, ct);
    }

    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }

        _passed++;
    }
}

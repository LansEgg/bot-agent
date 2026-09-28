using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Adapters.Platforms.Feishu;

public sealed record FeishuOutboxItem(long MessageId, string SourceKey, string Text, DateTimeOffset SentAt);

/// <summary>
/// 飞书平台网关与适配器（阶段 4：首个非 QQ 外部平台）。
/// </summary>
public sealed class FeishuBotGateway : IQqChatSource, IPlatformAdapter, IPlatformMessenger, IDisposable
{
    private const int OutboxCapacity = 200;
    private const int SeenCapacity = 500;

    private readonly SettingsBox _box;
    private readonly IHttpFetcher _http;
    private readonly Action<string>? _log;

    private readonly ConcurrentDictionary<string, long> _toAlias = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, string> _toOriginal = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FeishuOutboxItem> _outbox = new();

    private string _tenantToken = string.Empty;
    private DateTimeOffset _tokenExpires = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private long _nextMessageId = 1;
    private bool _disposed;

    public FeishuBotGateway(SettingsBox box, IHttpFetcher http, Action<string>? log = null)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log;

        Context = new PlatformContext(PlatformId.Feishu, AccountScope.Default, "feishu-main");
        Capabilities = PlatformCapabilities.FeishuTextOnly;
    }

    public PlatformContext Context { get; }
    public string DisplayName => "飞书";
    public string Tag => "飞书";
    public string Channel => Channels.Feishu;
    public PlatformCapabilities Capabilities { get; }
    public string? LastErrorCode { get; private set; }

    public bool IsConnected => _box.Current.FeishuEnabled && !string.IsNullOrWhiteSpace(_box.Current.FeishuAppId);

    public IReadOnlyList<FeishuOutboxItem> Outbox => _outbox.ToArray();

    public event Action<QqChatMessage>? MessageReceived;
    public event Action<InboundMessage>? InboundReceived;
    public event Action<QqPokeEvent>? Poked { add { } remove { } }
    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
    public event Action<bool>? ConnectionChanged;

    public long AliasFor(string original)
    {
        if (string.IsNullOrWhiteSpace(original)) return 0;
        if (_toAlias.TryGetValue(original, out var existing)) return existing;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(original));
        var slot = BitConverter.ToUInt32(hash, 0) % 1_000_000_000_000L;
        var candidate = Channels.FeishuBase + slot;
        var guard = 0;
        while (_toOriginal.ContainsKey(candidate) && guard++ < 1_000_000)
        {
            candidate++;
            if (candidate >= Channels.LocalBase) candidate = Channels.FeishuBase;
        }

        _toAlias[original] = candidate;
        _toOriginal[candidate] = original;
        return candidate;
    }

    public string? OriginalOf(long alias) => _toOriginal.TryGetValue(alias, out var orig) ? orig : null;

    public async Task<(bool Handled, int StatusCode, string ResponseBody)> HandleWebhookAsync(
        string body,
        string? signature,
        string? timestamp,
        string? nonce,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (false, 400, "{\"error\":\"empty_body\"}");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch
        {
            return (false, 400, "{\"error\":\"invalid_json\"}");
        }

        if (node is null) return (false, 400, "{\"error\":\"null_payload\"}");

        // URL 挑战握手
        var type = node["type"]?.GetValue<string>();
        if (string.Equals(type, "url_verification", StringComparison.OrdinalIgnoreCase))
        {
            var challenge = node["challenge"]?.GetValue<string>() ?? string.Empty;
            var token = node["token"]?.GetValue<string>() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_box.Current.FeishuVerificationToken)
                && !string.Equals(token, _box.Current.FeishuVerificationToken, StringComparison.Ordinal))
            {
                return (false, 403, "{\"error\":\"token_mismatch\"}");
            }
            return (true, 200, $"{{\"challenge\":\"{challenge}\"}}");
        }

        // 验签（若配置了签名密钥）
        if (!string.IsNullOrWhiteSpace(_box.Current.FeishuEncryptKey))
        {
            if (!VerifySignature(body, signature, timestamp, nonce, _box.Current.FeishuEncryptKey))
            {
                LastErrorCode = "signature_mismatch";
                return (false, 401, "{\"error\":\"signature_mismatch\"}");
            }
        }

        var header = node["header"];
        var eventType = header?["event_type"]?.GetValue<string>();
        var eventId = header?["event_id"]?.GetValue<string>() ?? string.Empty;

        if (!string.Equals(eventType, "im.message.receive_v1", StringComparison.OrdinalIgnoreCase))
        {
            return (true, 200, "{\"code\":0,\"msg\":\"ignored_event_type\"}");
        }

        var evt = node["event"];
        var messageNode = evt?["message"];
        var messageId = messageNode?["message_id"]?.GetValue<string>() ?? eventId;

        // 去重
        var dedupKey = string.IsNullOrEmpty(eventId) ? messageId : eventId;
        if (!string.IsNullOrEmpty(dedupKey))
        {
            if (_seen.ContainsKey(dedupKey))
            {
                return (true, 200, "{\"code\":0,\"msg\":\"duplicate\"}");
            }
            _seen[dedupKey] = Clock.Now;
            if (_seen.Count > SeenCapacity) _seen.Clear();
        }

        var chatType = messageNode?["chat_type"]?.GetValue<string>() ?? "group";
        var isGroup = !string.Equals(chatType, "p2p", StringComparison.OrdinalIgnoreCase);
        var chatId = messageNode?["chat_id"]?.GetValue<string>() ?? string.Empty;
        var senderOpenId = evt?["sender"]?["sender_id"]?["open_id"]?.GetValue<string>()
            ?? evt?["sender"]?["sender_id"]?["user_id"]?.GetValue<string>() ?? "unknown_user";

        var targetRaw = isGroup ? chatId : senderOpenId;
        if (string.IsNullOrWhiteSpace(targetRaw)) return (false, 400, "{\"error\":\"missing_target\"}");

        // 白名单检查
        if (!IsAllowed(targetRaw))
        {
            _log?.Invoke($"[飞书] 忽略未在白名单中的消息（目标={targetRaw}）");
            return (true, 200, "{\"code\":0,\"msg\":\"not_whitelisted\"}");
        }

        var contentStr = messageNode?["content"]?.GetValue<string>() ?? string.Empty;
        var text = ExtractText(contentStr);

        var mentions = messageNode?["mentions"]?.AsArray();
        var mentioned = mentions is { Count: > 0 } || text.StartsWith('@');

        var internalTarget = AliasFor(targetRaw);
        var internalSender = AliasFor(senderOpenId);
        var internalMsgId = AliasFor(messageId);

        var qqMsg = new QqChatMessage(
            MessageId: internalMsgId,
            IsGroup: isGroup,
            UserId: internalSender,
            GroupId: isGroup ? internalTarget : 0,
            SenderName: senderOpenId,
            Text: text,
            Time: Clock.Now,
            MentionedSelf: mentioned,
            Channel: Channels.Feishu);

        var convId = new ConversationId(PlatformId.Feishu, AccountScope.Default, isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, targetRaw);
        var inMsg = new InboundMessage(
            Ref: new MessageRef(convId, messageId),
            Sender: new ParticipantId(PlatformId.Feishu, AccountScope.Default, senderOpenId),
            SenderName: senderOpenId,
            Text: text,
            Timestamp: Clock.Now,
            IsMentioned: mentioned);

        _log?.Invoke($"[飞书] 收到入站消息（会话={targetRaw}，字数={text.Length}）");
        MessageReceived?.Invoke(qqMsg);
        InboundReceived?.Invoke(inMsg);
        return (true, 200, "{\"code\":0}");
    }

    public async Task<SendResult> SendTextAsync(
        bool isGroup,
        long targetId,
        string text,
        CancellationToken ct = default,
        long? replyToMessageId = null,
        bool directAddress = false)
    {
        var rawTarget = OriginalOf(targetId);
        if (string.IsNullOrEmpty(rawTarget))
        {
            return new SendResult(false, 0);
        }

        var conv = new ConversationId(PlatformId.Feishu, AccountScope.Default, isGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat, rawTarget);
        var rawReply = replyToMessageId.HasValue ? OriginalOf(replyToMessageId.Value) : null;
        var outMsg = new OutboundMessage(conv, text, rawReply);
        var res = await SendAsync(Context, outMsg, ct).ConfigureAwait(false);
        var numId = long.TryParse(res.MessageId, out var parsed) ? parsed : Interlocked.Increment(ref _nextMessageId);
        return new SendResult(res.IsSuccess, numId);
    }

    public async Task<DeliveryResult> SendAsync(
        PlatformContext context,
        OutboundMessage message,
        CancellationToken ct = default)
    {
        if (message is null) return DeliveryResult.Rejected("bad_request");

        var targetRaw = message.Target.NativeTargetId;
        var token = await GetTenantTokenAsync(ct).ConfigureAwait(false);
        var baseUri = string.IsNullOrWhiteSpace(_box.Current.FeishuApiBase)
            ? "https://open.feishu.cn"
            : _box.Current.FeishuApiBase.TrimEnd('/');

        var isGroup = message.Target.Kind == ConversationKind.GroupChat;
        var receiveType = isGroup ? "chat_id" : "open_id";
        var url = string.IsNullOrWhiteSpace(message.ReplyToMessageId)
            ? $"{baseUri}/open-apis/im/v1/messages?receive_id_type={receiveType}"
            : $"{baseUri}/open-apis/im/v1/messages/{message.ReplyToMessageId}/reply";

        var payload = new JsonObject
        {
            ["msg_type"] = "text",
            ["content"] = new JsonObject { ["text"] = message.Text }.ToJsonString(),
        };
        if (string.IsNullOrWhiteSpace(message.ReplyToMessageId))
        {
            payload["receive_id"] = targetRaw;
        }

        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        try
        {
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 429)
            {
                LastErrorCode = "throttled";
                return DeliveryResult.Throttled(5, "feishu_rate_limited");
            }

            var respText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var respNode = JsonNode.Parse(respText);
            var code = respNode?["code"]?.GetValue<int>() ?? (resp.IsSuccessStatusCode ? 0 : -1);

            if (code == 0)
            {
                var mid = respNode?["data"]?["message_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                var key = ConversationIdCodec.Encode(message.Target);
                _outbox.Enqueue(new FeishuOutboxItem(AliasFor(mid), key, message.Text, Clock.Now));
                while (_outbox.Count > OutboxCapacity) _outbox.TryDequeue(out _);
                _log?.Invoke($"[飞书] 消息已投递（会话={targetRaw}，长度={message.Text.Length}）");
                return DeliveryResult.Ok(mid);
            }

            LastErrorCode = $"feishu_err_{code}";
            return DeliveryResult.Transient($"feishu_err_{code}", respText);
        }
        catch (Exception ex)
        {
            LastErrorCode = "network_error";
            _log?.Invoke($"[飞书] 出站请求异常: {ex.Message}");
            return DeliveryResult.Transient("network_error", ex.Message);
        }
    }

    public Task<(string? Text, string? SenderId)> GetQuotedMessageAsync(MessageRef messageRef, CancellationToken ct = default)
        => Task.FromResult<(string?, string?)>((null, null));

    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<(string?, long)>((null, 0));

    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => Task.FromResult(false);

    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public void RegisterTarget(string channel, bool isGroup, long id) { }

    private bool IsAllowed(string target)
    {
        var wl = _box.Current.FeishuWhitelist;
        if (string.IsNullOrWhiteSpace(wl)) return true; // 空白按全接受
        var tokens = wl.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Contains(target, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> GetTenantTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_tenantToken) && Clock.Now < _tokenExpires)
        {
            return _tenantToken;
        }

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_tenantToken) && Clock.Now < _tokenExpires) return _tenantToken;
            if (string.IsNullOrWhiteSpace(_box.Current.FeishuAppId) || string.IsNullOrWhiteSpace(_box.Current.FeishuAppSecret))
            {
                return string.Empty;
            }

            var baseUri = string.IsNullOrWhiteSpace(_box.Current.FeishuApiBase)
                ? "https://open.feishu.cn"
                : _box.Current.FeishuApiBase.TrimEnd('/');
            var url = $"{baseUri}/open-apis/auth/v3/tenant_access_token/internal";
            var payload = new JsonObject
            {
                ["app_id"] = _box.Current.FeishuAppId,
                ["app_secret"] = _box.Current.FeishuAppSecret,
            };

            var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return string.Empty;

            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var token = json?["tenant_access_token"]?.GetValue<string>();
            var expire = json?["expire"]?.GetValue<int>() ?? 7200;
            if (!string.IsNullOrEmpty(token))
            {
                _tenantToken = token;
                _tokenExpires = Clock.Now.AddSeconds(Math.Max(60, expire - 300));
                ConnectionChanged?.Invoke(true);
                return token;
            }

            return string.Empty;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static string ExtractText(string contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson)) return string.Empty;
        try
        {
            var node = JsonNode.Parse(contentJson);
            return node?["text"]?.GetValue<string>() ?? contentJson;
        }
        catch
        {
            return contentJson;
        }
    }

    private static bool VerifySignature(string body, string? signature, string? timestamp, string? nonce, string encryptKey)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(nonce))
        {
            return false;
        }

        var raw = timestamp + nonce + encryptKey + body;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(hex, signature.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokenGate.Dispose();
    }
}

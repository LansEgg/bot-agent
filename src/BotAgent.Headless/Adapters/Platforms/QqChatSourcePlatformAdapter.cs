using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Adapters.Platforms;

/// <summary>
/// 将既有 <see cref="IQqChatSource"/> 包装为平台中立的 <see cref="IPlatformAdapter"/> 与 <see cref="IPlatformMessenger"/>。
/// </summary>
public sealed class QqChatSourcePlatformAdapter : IPlatformAdapter, IPlatformMessenger, IDisposable
{
    private readonly IQqChatSource _source;
    private bool _disposed;

    public QqChatSourcePlatformAdapter(
        IQqChatSource source,
        PlatformContext context,
        PlatformCapabilities capabilities,
        string displayName,
        string tag)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        DisplayName = displayName;
        Tag = tag;

        _source.MessageReceived += OnMessageReceived;
        _source.ConnectionChanged += OnConnectionChanged;
    }

    public PlatformContext Context { get; }

    public string DisplayName { get; }

    public string Tag { get; }

    public bool IsConnected => _source.IsConnected;

    public PlatformCapabilities Capabilities { get; }

    public string? LastErrorCode { get; private set; }

    public event Action<InboundMessage>? InboundReceived;

    public event Action<bool>? ConnectionChanged;

    public async Task<DeliveryResult> SendAsync(
        PlatformContext context,
        OutboundMessage message,
        CancellationToken ct = default)
    {
        if (context is null || message is null)
        {
            return DeliveryResult.Rejected("bad_request");
        }

        var expectedPlatform = PlatformId.Normalize(Context.PlatformId);
        if (!string.Equals(PlatformId.Normalize(context.PlatformId), expectedPlatform, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(PlatformId.Normalize(message.Target.PlatformId), expectedPlatform, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.AccountScope, Context.AccountScope, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(message.Target.AccountScope, Context.AccountScope, StringComparison.OrdinalIgnoreCase))
        {
            LastErrorCode = "context_mismatch";
            return DeliveryResult.Rejected("context_mismatch");
        }

        if (!long.TryParse(message.Target.NativeTargetId, out var targetId) || targetId <= 0)
        {
            LastErrorCode = "invalid_target_id";
            return DeliveryResult.Rejected("invalid_target_id");
        }

        var isGroup = message.Target.Kind == ConversationKind.GroupChat;
        var wantsImage = message.ImageData is { Length: > 0 };
        var wantsVoice = !string.IsNullOrWhiteSpace(message.VoiceUrl);
        var wantsQuote = !string.IsNullOrWhiteSpace(message.ReplyToMessageId);
        var deg = Capabilities.EvaluateDegradation(wantsImage, wantsVoice, wantsQuote);

        long? replyToId = null;
        if (deg.SendQuote && long.TryParse(message.ReplyToMessageId, out var parsedReply) && parsedReply > 0)
        {
            replyToId = parsedReply;
        }

        if (deg.SendVoice && !string.IsNullOrWhiteSpace(message.VoiceUrl))
        {
            var voiceOk = await _source.SendVoiceAsync(isGroup, targetId, message.VoiceUrl!, ct).ConfigureAwait(false);
            if (voiceOk && string.IsNullOrWhiteSpace(message.Text))
            {
                return DeliveryResult.Ok();
            }
        }

        if (deg.SendImage && message.ImageData is { Length: > 0 })
        {
            var imgOk = await _source.SendImageAsync(isGroup, targetId, message.ImageData, ct, replyToId).ConfigureAwait(false);
            if (imgOk && string.IsNullOrWhiteSpace(message.Text))
            {
                return DeliveryResult.Ok();
            }
        }

        var textToSend = message.Text;
        if (string.IsNullOrWhiteSpace(textToSend) && wantsImage && !deg.SendImage && !string.IsNullOrWhiteSpace(message.ImageDescription))
        {
            textToSend = $"[表情/图片: {message.ImageDescription}]";
        }

        if (string.IsNullOrWhiteSpace(textToSend))
        {
            return deg.IsDegraded
                ? DeliveryResult.Rejected(deg.ReasonCodes[0])
                : DeliveryResult.Rejected("empty_content");
        }

        var sendRes = await _source.SendTextAsync(isGroup, targetId, textToSend, ct, replyToId).ConfigureAwait(false);
        if (!sendRes.Ok)
        {
            LastErrorCode = "send_failed";
            return DeliveryResult.Transient("send_failed");
        }

        var msgIdStr = sendRes.MessageId > 0 ? sendRes.MessageId.ToString() : null;
        if (deg.IsDegraded)
        {
            return DeliveryResult.Degraded(msgIdStr, string.Join(",", deg.ReasonCodes));
        }

        return DeliveryResult.Ok(msgIdStr);
    }

    public async Task<(string? Text, string? SenderId)> GetQuotedMessageAsync(
        MessageRef messageRef,
        CancellationToken ct = default)
    {
        if (messageRef is null)
        {
            return (null, null);
        }

        var expectedPlatform = PlatformId.Normalize(Context.PlatformId);
        if (!string.Equals(PlatformId.Normalize(messageRef.Conversation.PlatformId), expectedPlatform, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        if (!long.TryParse(messageRef.NativeMessageId, out var msgId) || msgId <= 0)
        {
            return (null, null);
        }

        var (text, senderId) = await _source.GetMessageInfoAsync(msgId, ct).ConfigureAwait(false);
        return (text, senderId > 0 ? senderId.ToString() : null);
    }

    private void OnMessageReceived(QqChatMessage msg)
    {
        var kind = msg.IsGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat;
        var nativeTarget = (msg.IsGroup ? msg.GroupId : msg.UserId).ToString();
        var convId = new ConversationId(Context.PlatformId, Context.AccountScope, kind, nativeTarget);
        var msgRef = new MessageRef(convId, msg.MessageId.ToString());
        var sender = new ParticipantId(Context.PlatformId, Context.AccountScope, msg.UserId.ToString());
        var inbound = new InboundMessage(
            Ref: msgRef,
            Sender: sender,
            SenderName: msg.SenderName,
            Text: msg.Text,
            Timestamp: msg.Time,
            ReplyToMessageId: msg.ReplyToMessageId?.ToString(),
            IsMentioned: msg.MentionedSelf,
            ImageUrls: msg.ImageUrls);
        InboundReceived?.Invoke(inbound);
    }

    private void OnConnectionChanged(bool connected) => ConnectionChanged?.Invoke(connected);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.MessageReceived -= OnMessageReceived;
        _source.ConnectionChanged -= OnConnectionChanged;
    }
}

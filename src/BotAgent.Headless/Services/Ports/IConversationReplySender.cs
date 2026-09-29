using BotAgent.Domain.Platforms;

namespace BotAgent.Services.Ports;

/// <summary>
/// 平台中立的会话回复发送端口别名（指向 <see cref="IQqMessageSender"/>），支持通用调用方渐进迁移。
/// </summary>
public interface IConversationReplySender : IQqMessageSender
{
    /// <summary>支持显式平台上下文的出站分句发送重载，优先使用显式上下文判定策略。</summary>
    Task<CadenceSendReport> SendWithCadenceAsync(
        PlatformContext context,
        bool isGroup,
        long targetId,
        string reply,
        long? replyTo,
        bool directAddress = false);
}

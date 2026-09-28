namespace BotAgent.Services.Ports;

/// <summary>
/// 平台中立的会话回复发送端口别名（指向 <see cref="IQqMessageSender"/>），支持通用调用方渐进迁移。
/// </summary>
public interface IConversationReplySender : IQqMessageSender
{
}

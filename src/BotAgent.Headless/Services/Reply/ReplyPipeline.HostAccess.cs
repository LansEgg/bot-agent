using BotAgent.Domain.Conversation;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    /// <summary>丢掉某会话的待回复项（删会话/改白名单时用）。在途那次不中斷，但不再补发后续。</summary>
    private void DropPending(string sourceKey)
    {
        _pendingReplies.TryRemove(sourceKey, out _);
        _pendingConversations.TryRemove(sourceKey, out _);
    }

    // ══════════ 宿主（BotAgentHost）与面板要用的公开入口 ══════════

    /// <summary>面板/宿主读的在途与排队计数。</summary>
    public int InFlightReplies => _inFlight.Count;

    public int QueuedReplies => _pendingReplies.Values.Sum(q => q.Count);

    public long LastGenerationMilliseconds => Interlocked.Read(ref _lastGenerationMs);

    /// <summary>按 sourceKey 找会话（agent 结果回来时只能用 key）。</summary>
    public bool TryFind(string sourceKey, out BotConversation conversation)
    {
        conversation = _registry.Find(sourceKey)!;
        return conversation is not null;
    }

    /// <summary>会话被删 / 移出白名单：把它的**回复侧**痕迹一起清掉（冷却、历史标记、待回复队列）。</summary>
    public void ForgetSession(string sourceKey)
    {
        _replyCooldown.TryRemove(sourceKey, out _);
        _historyRequested.TryRemove(sourceKey, out _);
        DropPending(sourceKey);
    }

    /// <summary>恢复出来的会话：历史按"还没拉过"算（首次收到消息时才去拉群历史）。</summary>
    public void MarkHistoryPending(string sourceKey) => _historyRequested.TryAdd(sourceKey, 0);

    /// <summary>面板改了并发上限 → 换一个新的闸门（旧的不 Dispose，让在途的那次跑完）。</summary>
    public void ResizeGate(int permits)
    {
        if (permits == _replyGatePermits)
        {
            return;
        }

        _replyGatePermits = permits;
        var previous = _replyGate;
        _replyGate = new SemaphoreSlim(permits);
        RetireGate(previous);
        _hooks.Log($"模型并发上限已改为 {permits}");
    }
}

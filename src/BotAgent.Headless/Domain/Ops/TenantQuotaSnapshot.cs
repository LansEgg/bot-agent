namespace BotAgent.Domain.Ops;

/// <summary>单租户每日 Token 配额与节能静默状态快照。</summary>
public readonly record struct TenantQuotaSnapshot(
    string TenantId,
    int DailyTokenLimit,
    int UsedPromptTokens,
    int UsedCompletionTokens,
    string ResetDate,
    bool EnergySaving);

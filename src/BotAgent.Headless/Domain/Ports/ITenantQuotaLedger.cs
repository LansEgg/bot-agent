namespace BotAgent.Domain.Ports;

/// <summary>多租户每日配额账本端口（供回复链判定节能静默与记录 token 用量）。</summary>
public interface ITenantQuotaLedger
{
    /// <summary>当前租户是否因达到当日 Token 配额上限而处于节能静默模式。</summary>
    bool IsEnergySaving(string tenantId);

    /// <summary>记录当前租户本轮消耗的 Token 用量。</summary>
    void RecordUsage(string tenantId, int promptTokens, int completionTokens);
}

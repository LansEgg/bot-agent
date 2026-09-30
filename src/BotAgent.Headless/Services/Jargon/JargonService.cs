using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using BotAgent.Domain.Jargon;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Jargon;

/// <summary>
/// 圈子黑话/俚语管理与自动识别提取服务（对标 MaiBot Jargon 机制）。
/// 包含内存热度缓冲、Token 预算与冷却保护，严格保护用户隐私。
/// </summary>
public sealed class JargonService
{
    private readonly IJargonRepository _repo;
    private readonly SettingsBox _settings;

    // 内存频率缓冲：scope -> (phrase -> hitCount)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _phraseCounters = new();

    // 节流冷却：scope -> 上次提炼时刻
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastExtractTime = new();

    private static readonly TimeSpan ExtractCooldown = TimeSpan.FromMinutes(15);
    private const int MinOccurrencesToPropose = 2;

    public JargonService(IJargonRepository repo, SettingsBox settings)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// 观察一条入站群聊消息，在后台提取并记录潜在黑话候选词（非阻塞、安全脱敏）。
    /// </summary>
    public void ObserveMessage(string scope, string? text)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var candidates = JargonFilterRules.ExtractCandidatePhrases(text);
        if (candidates.Count == 0)
        {
            return;
        }

        // 防会话数量无限膨胀（有界台账：最多跟踪 500 个会话）
        if (_phraseCounters.Count > 500 && !_phraseCounters.ContainsKey(scope))
        {
            var first = System.Linq.Enumerable.FirstOrDefault(_phraseCounters.Keys);
            if (first != null) _phraseCounters.TryRemove(first, out _);
        }

        var scopeCounter = _phraseCounters.GetOrAdd(scope, _ => new ConcurrentDictionary<string, int>());

        // 防单群词汇无限膨胀（单群候选词上限 200，超出时淘汰低频项）
        if (scopeCounter.Count > 200)
        {
            foreach (var key in scopeCounter.Keys)
            {
                if (scopeCounter.TryGetValue(key, out var hits) && hits <= 1)
                {
                    scopeCounter.TryRemove(key, out _);
                }
            }
        }

        foreach (var phrase in candidates)
        {
            scopeCounter.AddOrUpdate(phrase, 1, (_, count) => count + 1);
        }
    }

    /// <summary>
    /// 执行指定作用域的黑话发现与提炼沉淀（受冷却时间与频次门槛保护）。
    /// </summary>
    public async Task<int> DiscoverAndPersistAsync(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return 0;
        }

        var now = Clock.Now;
        if (_lastExtractTime.TryGetValue(scope, out var last) && now - last < ExtractCooldown)
        {
            return 0; // 冷却中，避免频繁调度
        }

        if (!_phraseCounters.TryGetValue(scope, out var counter) || counter.IsEmpty)
        {
            return 0;
        }

        _lastExtractTime[scope] = now;
        int persistedCount = 0;

        foreach (var kvp in counter)
        {
            var phrase = kvp.Key;
            var hits = kvp.Value;

            if (hits >= MinOccurrencesToPropose)
            {
                var existing = await _repo.GetAsync(scope, phrase).ConfigureAwait(false);
                if (existing is null)
                {
                    // 首次发现，以 Pending 待审核状态落库
                    await _repo.UpsertAsync(new JargonEntry
                    {
                        Scope = scope,
                        Phrase = phrase,
                        Meaning = $"在群聊中高频出现的流行词汇/梗（累计出现 {hits} 次）",
                        Status = JargonStatus.Pending,
                        HitCount = hits,
                        CreatedAt = now,
                        UpdatedAt = now
                    }).ConfigureAwait(false);

                    persistedCount++;
                }
                else
                {
                    // 已存在，增量更新频次
                    existing.HitCount += hits;
                    existing.UpdatedAt = now;
                    await _repo.UpsertAsync(existing).ConfigureAwait(false);
                }

                // 消费后重置内存计数
                counter.TryRemove(phrase, out _);
            }
        }

        return persistedCount;
    }

    /// <summary>
    /// 渲染可注入 Prompt 的黑话字典说明段落（仅包含已确认或手动录入的条目）。
    /// </summary>
    public async Task<string?> RenderPromptSnippetAsync(string scope, int limit = 15)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        var confirmed = await _repo.ListConfirmedForPromptAsync(scope, limit).ConfigureAwait(false);
        if (confirmed.Count == 0)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\n\n[本群圈子黑话/俚语字典]");
        sb.AppendLine("请自然理解并在契合语境时模仿群友使用以下黑话，避免机械生硬解释：");
        foreach (var item in confirmed)
        {
            sb.AppendLine($"• {item.Phrase}：{item.Meaning}");
        }

        return sb.ToString().TrimEnd();
    }
}

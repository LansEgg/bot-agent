using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Ops;
using BotAgent.Services.Ops;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private async Task HandleQuotaGetAsync(HttpListenerContext context)
    {
        var tenant = context.Request.QueryString["tenant"]?.Trim();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "tenant is required" });
            return;
        }

        if (_registry.Find(tenant) is null)
        {
            await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "tenant is not registered" });
            return;
        }

        await WriteJsonAsync(context, 200, BuildQuotaPayload(_quotas.GetQuota(tenant)));
    }

    private async Task HandleQuotaSaveAsync(HttpListenerContext context)
    {
        var body = await ReadJsonAsync(context) as JsonObject;
        if (body is null
            || !TryReadString(body, "tenant", out var tenant)
            || !TryReadInt(body, "dailyTokenLimit", out var dailyTokenLimit))
        {
            await WriteJsonAsync(context, 400, new JsonObject
            {
                ["error"] = "tenant and integer dailyTokenLimit are required"
            });
            return;
        }

        tenant = tenant.Trim();
        if (tenant.Length == 0)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "tenant is required" });
            return;
        }

        if (_registry.Find(tenant) is null)
        {
            await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "tenant is not registered" });
            return;
        }

        if (!TenantQuotaPolicy.IsValidDailyTokenLimit(dailyTokenLimit))
        {
            await WriteJsonAsync(context, 400, new JsonObject
            {
                ["error"] = $"dailyTokenLimit must be between {TenantQuotaPolicy.MinimumDailyTokenLimit} and {TenantQuotaPolicy.MaximumDailyTokenLimit}",
                ["minimumDailyTokenLimit"] = TenantQuotaPolicy.MinimumDailyTokenLimit,
                ["maximumDailyTokenLimit"] = TenantQuotaPolicy.MaximumDailyTokenLimit
            });
            return;
        }

        var before = _quotas.GetQuota(tenant);
        var snapshot = _quotas.SetDailyTokenLimit(tenant, dailyTokenLimit);
        AppendQuotaAudit(context, tenant, before.DailyTokenLimit, snapshot.DailyTokenLimit);
        var payload = BuildQuotaPayload(snapshot);
        payload["ok"] = true;
        await WriteJsonAsync(context, 200, payload);
    }

    private void AppendQuotaAudit(HttpListenerContext context, string tenant, int oldLimit, int newLimit)
    {
        if (_auditChain is null)
        {
            return;
        }

        var detail = new JsonObject
        {
            ["result"] = "applied",
            ["resource"] = "tenant_quota",
            ["tenant"] = _dto.Mask(tenant),
            ["oldDailyTokenLimit"] = oldLimit,
            ["newDailyTokenLimit"] = newLimit
        }.ToJsonString();
        _auditChain.Append(new AuditEvent("config_change", PanelAuditActor(context), "panel", detail, "2.1"));
    }

    private static string PanelAuditActor(HttpListenerContext context)
    {
        var presented = context.Request.Cookies["panel_session"]?.Value
            ?? context.Request.Headers["X-Panel-Token"]
            ?? context.Request.QueryString["token"];
        if (string.IsNullOrWhiteSpace(presented))
        {
            return "panel:unknown";
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presented.Trim())))
            .ToLowerInvariant()[..16];
        return "panel-session:" + fingerprint;
    }

    private static JsonObject BuildQuotaPayload(TenantQuotaSnapshot snapshot)
    {
        var nextReset = DateTimeOffset.TryParseExact(
            snapshot.ResetDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var resetDate)
            ? resetDate.AddDays(1)
            : new DateTimeOffset(Clock.UtcNow.Date.AddDays(1), TimeSpan.Zero);
        var reason = snapshot.EnergySaving ? "quota_energy_saving" : null;

        return new JsonObject
        {
            ["tenant"] = snapshot.TenantId,
            ["dailyTokenLimit"] = snapshot.DailyTokenLimit,
            ["usedTokens"] = snapshot.UsedTotalTokens,
            ["usedPromptTokens"] = snapshot.UsedPromptTokens,
            ["usedCompletionTokens"] = snapshot.UsedCompletionTokens,
            ["remainingTokens"] = snapshot.RemainingTokens,
            ["resetDate"] = snapshot.ResetDate,
            ["nextResetAt"] = nextReset.ToString("O", CultureInfo.InvariantCulture),
            ["energySaving"] = snapshot.EnergySaving,
            ["reason"] = reason,
            ["energySavingReason"] = reason,
            ["minimumDailyTokenLimit"] = TenantQuotaPolicy.MinimumDailyTokenLimit,
            ["maximumDailyTokenLimit"] = TenantQuotaPolicy.MaximumDailyTokenLimit
        };
    }

    private static bool TryReadString(JsonObject body, string name, out string value)
    {
        if (body[name] is JsonValue node && node.TryGetValue<string>(out var raw))
        {
            value = raw ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadInt(JsonObject body, string name, out int value)
    {
        value = 0;
        return body[name] is JsonValue node && node.TryGetValue<int>(out value);
    }
}

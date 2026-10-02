using System;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BotAgent.Domain.Jargon;
using BotAgent.Services;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private async Task HandleJargonsAsync(HttpListenerContext context, string path, string method)
    {
        var sub = path.Length > "/api/jargons".Length
            ? path["/api/jargons".Length..].TrimStart('/')
            : string.Empty;

        if (string.IsNullOrEmpty(sub))
        {
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var scope = context.Request.QueryString["scope"] ?? "global";
                var statusStr = context.Request.QueryString["status"];
                JargonStatus? status = Enum.TryParse<JargonStatus>(statusStr, true, out var parsedStatus)
                    ? parsedStatus
                    : null;

                var items = await _jargons.ListByScopeAsync(scope, status).ConfigureAwait(false);
                var arr = new JsonArray();
                foreach (var item in items)
                {
                    arr.Add(new JsonObject
                    {
                        ["id"] = item.Id,
                        ["scope"] = _dto.Mask(item.Scope),
                        ["scopeRaw"] = item.Scope,
                        ["phrase"] = item.Phrase,
                        ["meaning"] = item.Meaning,
                        ["status"] = item.Status.ToString().ToLowerInvariant(),
                        ["hitCount"] = item.HitCount,
                        ["updatedUnix"] = item.UpdatedAt.ToUnixTimeSeconds()
                    });
                }

                await WriteJsonAsync(context, 200, new JsonObject { ["items"] = arr }).ConfigureAwait(false);
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadJsonAsync(context).ConfigureAwait(false);
                if (body is null)
                {
                    await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "bad_request" }).ConfigureAwait(false);
                    return;
                }

                var phrase = body["phrase"]?.GetValue<string>()?.Trim();
                var meaning = body["meaning"]?.GetValue<string>()?.Trim();
                var scope = body["scope"]?.GetValue<string>()?.Trim() ?? "global";

                if (string.IsNullOrWhiteSpace(phrase) || string.IsNullOrWhiteSpace(meaning))
                {
                    await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "phrase_and_meaning_required" }).ConfigureAwait(false);
                    return;
                }

                var entry = new JargonEntry
                {
                    Scope = scope,
                    Phrase = phrase,
                    Meaning = meaning,
                    Status = JargonStatus.Manual,
                    HitCount = 1,
                    CreatedAt = Clock.Now,
                    UpdatedAt = Clock.Now
                };
                await _jargons.UpsertAsync(entry).ConfigureAwait(false);
                _ui.EmitLog($"黑话录入：[{scope}] {phrase}（{meaning}）");
                await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            var parts = sub.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Equals("status", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[0], out var updateId))
            {
                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    var body = await ReadJsonAsync(context).ConfigureAwait(false);
                    var statusStr = body?["status"]?.GetValue<string>();
                    if (Enum.TryParse<JargonStatus>(statusStr, true, out var newStatus))
                    {
                        await _jargons.SetStatusAsync(updateId, newStatus).ConfigureAwait(false);
                        _ui.EmitLog($"黑话 #{updateId} 状态变更为 {newStatus}");
                        await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
                        return;
                    }
                    await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "invalid_status" }).ConfigureAwait(false);
                    return;
                }
            }

            if (parts.Length == 1 && long.TryParse(parts[0], out var deleteId))
            {
                if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) || method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    await _jargons.DeleteAsync(deleteId).ConfigureAwait(false);
                    _ui.EmitLog($"黑话 #{deleteId} 已删除");
                    await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
                    return;
                }
            }
        }

        await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "not_found" }).ConfigureAwait(false);
    }
}

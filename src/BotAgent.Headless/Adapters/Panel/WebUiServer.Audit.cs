using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Services.Ops;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private Task HandleAuditVerifyAsync(HttpListenerContext context)
    {
        if (_auditChain is null)
        {
            return WriteJsonAsync(context, 503, new JsonObject { ["available"] = false });
        }

        var result = _auditChain.Verify();
        var payload = new JsonObject
        {
            ["available"] = true,
            ["valid"] = result.Valid,
            ["checkedCount"] = result.CheckedCount,
            ["breakpointId"] = result.BreakpointId,
            ["errorType"] = result.ErrorType
        };
        return WriteJsonAsync(context, result.Valid ? 200 : 409, payload);
    }

    private AuditEvent? BuildSettingsAuditEvent(JsonNode body)
    {
        if (_auditChain is null)
        {
            return null;
        }

        var fields = body.AsObject().Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var detail = new JsonObject
        {
            ["result"] = "applied",
            ["resource"] = "runtime_settings",
            ["fieldCount"] = fields.Length,
            ["fields"] = new JsonArray(fields.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray())
        }.ToJsonString();
        return new AuditEvent("config_change", "panel", "panel", detail, "2.1");
    }

    private void AppendSecretRotateAudit(string secretName, string action)
    {
        if (_auditChain is null)
        {
            return;
        }

        var detail = new JsonObject
        {
            ["result"] = action,
            ["resource"] = secretName
        }.ToJsonString();
        _auditChain.Append(new AuditEvent("secret_rotate", "panel", "panel", detail, "2.1"));
    }
}

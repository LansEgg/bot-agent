using System.Net;
using System.Text.Json.Nodes;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private async Task HandlePlatformsAsync(HttpListenerContext context)
    {
        var snapshots = _platformRegistry?.GetSnapshots() ?? Array.Empty<Domain.Ports.PlatformStatusSnapshot>();
        var arr = new JsonArray();
        foreach (var s in snapshots)
        {
            arr.Add(new JsonObject
            {
                ["platformId"] = s.PlatformId,
                ["accountScope"] = s.AccountScope,
                ["displayName"] = s.DisplayName,
                ["tag"] = s.Tag,
                ["enabled"] = s.Enabled,
                ["connected"] = s.Connected,
                ["lastErrorCode"] = s.LastErrorCode,
                ["capabilities"] = new JsonObject
                {
                    ["supportsText"] = s.Capabilities.SupportsText,
                    ["supportsImage"] = s.Capabilities.SupportsImage,
                    ["supportsVoice"] = s.Capabilities.SupportsVoice,
                    ["supportsQuote"] = s.Capabilities.SupportsQuote,
                    ["supportsRecall"] = s.Capabilities.SupportsRecall,
                    ["supportsGroup"] = s.Capabilities.SupportsGroup,
                    ["supportsDirect"] = s.Capabilities.SupportsDirect,
                    ["supportsThread"] = s.Capabilities.SupportsThread,
                },
            });
        }

        var feishuObj = new JsonObject
        {
            ["enabled"] = _box.Current.FeishuEnabled,
            ["configured"] = !string.IsNullOrWhiteSpace(_box.Current.FeishuAppId),
            ["connected"] = _feishuGateway?.IsConnected ?? false,
            ["outboxCount"] = _feishuGateway?.Outbox.Count ?? 0,
        };

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["platforms"] = arr,
            ["feishu"] = feishuObj,
        });
    }

    private async Task HandleFeishuWebhookAsync(HttpListenerContext context)
    {
        if (_feishuGateway is null || !_box.Current.FeishuEnabled)
        {
            await WriteJsonAsync(context, 403, new JsonObject
            {
                ["error"] = "feishu_channel_disabled",
                ["reason"] = "feishu_disabled",
            });
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? System.Text.Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        var sig = context.Request.Headers["X-Lark-Signature"];
        var ts = context.Request.Headers["X-Lark-Request-Timestamp"];
        var nonce = context.Request.Headers["X-Lark-Request-Nonce"];

        var (handled, statusCode, responseBody) = await _feishuGateway.HandleWebhookAsync(body, sig, ts, nonce);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var bytes = System.Text.Encoding.UTF8.GetBytes(responseBody);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }
}

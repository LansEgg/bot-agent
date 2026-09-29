using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Services.Platforms;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private const int MaxFeishuWebhookBodyBytes = 1_048_576;
    private static readonly SemaphoreSlim FeishuWebhookSlots = new(16, 16);
    private async Task HandlePlatformsAsync(HttpListenerContext context)
    {
        var snapshots = _platformRegistry?.GetSnapshots() ?? Array.Empty<Domain.Ports.PlatformStatusSnapshot>();
        var arr = new JsonArray();
        var policies = _platformPolicies ?? new PlatformPolicyResolver(_box, _platformRegistry);
        foreach (var s in snapshots)
        {
            var policy = policies.Resolve(new Domain.Platforms.PlatformContext(s.PlatformId, s.AccountScope));
            arr.Add(new JsonObject
            {
                ["platformId"] = s.PlatformId,
                ["accountScope"] = s.AccountScope,
                ["displayName"] = s.DisplayName,
                ["tag"] = s.Tag,
                ["enabled"] = s.Enabled,
                ["effectiveEnabled"] = policy.Enabled,
                ["chatEnabled"] = policy.ChatEnabled,
                ["connected"] = s.Connected,
                ["lastErrorCode"] = s.LastErrorCode,
                ["reasons"] = new JsonArray(policy.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
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
                    ["supportsStickers"] = s.Capabilities.SupportsStickers,
                    ["supportsMusic"] = s.Capabilities.SupportsMusic,
                    ["supportsPoke"] = s.Capabilities.SupportsPoke,
                },
            });
        }

        var feishuLoaded = _feishuGateway is not null;
        var feishuEnabled = _box.Current.FeishuEnabled;
        var feishuConfigured = _feishuGateway?.IsConfigured
            ?? (!string.IsNullOrWhiteSpace(_box.Current.FeishuAppId)
                && !string.IsNullOrWhiteSpace(_box.Current.FeishuAppSecret)
                && (!string.IsNullOrWhiteSpace(_box.Current.FeishuEncryptKey)
                    || !string.IsNullOrWhiteSpace(_box.Current.FeishuVerificationToken)));
        var feishuRestartRequired = feishuEnabled != feishuLoaded;
        var feishuObj = new JsonObject
        {
            ["enabled"] = feishuEnabled,
            ["configured"] = feishuConfigured,
            ["loaded"] = feishuLoaded,
            ["effectiveEnabled"] = feishuEnabled && feishuLoaded,
            ["restartRequired"] = feishuRestartRequired,
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
        if (_feishuGateway is null)
        {
            await WriteJsonAsync(context, 503, new JsonObject
            {
                ["error"] = "feishu_gateway_not_loaded",
                ["reason"] = "restart_required",
            });
            return;
        }

        if (!_box.Current.FeishuEnabled)
        {
            await WriteJsonAsync(context, 403, new JsonObject
            {
                ["error"] = "feishu_channel_disabled",
                ["reason"] = "feishu_disabled",
            });
            return;
        }

        if (context.Request.ContentLength64 > MaxFeishuWebhookBodyBytes)
        {
            await WriteJsonAsync(context, 413, new JsonObject
            {
                ["error"] = "body_too_large",
                ["maxBytes"] = MaxFeishuWebhookBodyBytes,
            });
            return;
        }

        if (!await FeishuWebhookSlots.WaitAsync(0, _cts.Token).ConfigureAwait(false))
        {
            await WriteJsonAsync(context, 429, new JsonObject
            {
                ["error"] = "webhook_overloaded",
            });
            return;
        }

        try
        {
            var body = await ReadFeishuBodyAsync(context.Request.InputStream, context.Request.ContentEncoding, _cts.Token)
                .ConfigureAwait(false);
            if (body is null)
            {
                await WriteJsonAsync(context, 413, new JsonObject
                {
                    ["error"] = "body_too_large",
                    ["maxBytes"] = MaxFeishuWebhookBodyBytes,
                });
                return;
            }

            var sig = context.Request.Headers["X-Lark-Signature"];
            var ts = context.Request.Headers["X-Lark-Request-Timestamp"];
            var nonce = context.Request.Headers["X-Lark-Request-Nonce"];
            var verificationToken = context.Request.Headers["X-Lark-Verification-Token"];
            var (_, statusCode, responseBody) = await _feishuGateway.HandleWebhookAsync(
                body, sig, ts, nonce, verificationToken, _cts.Token).ConfigureAwait(false);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            var bytes = System.Text.Encoding.UTF8.GetBytes(responseBody);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // 宿主停止时不再尝试写响应。
        }
        finally
        {
            FeishuWebhookSlots.Release();
        }
    }

    private static async Task<string?> ReadFeishuBodyAsync(
        Stream input,
        System.Text.Encoding? encoding,
        CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        await using var memory = new MemoryStream();
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaxFeishuWebhookBodyBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return (encoding ?? System.Text.Encoding.UTF8).GetString(memory.ToArray());
    }
}

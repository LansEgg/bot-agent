using System.Text.Json.Nodes;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private JsonObject BuildPluginsPayload()
    {
        var list = _plugins?.GetAll() ?? Array.Empty<PluginInfo>();
        var arr = new JsonArray();
        foreach (var p in list)
        {
            arr.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["version"] = p.Version,
                ["description"] = p.Description,
                ["category"] = p.Category.ToString(),
                ["isPreset"] = p.IsPreset,
                ["isEnabled"] = p.IsEnabled
            });
        }

        return new JsonObject
        {
            ["total"] = list.Count,
            ["plugins"] = arr
        };
    }
}

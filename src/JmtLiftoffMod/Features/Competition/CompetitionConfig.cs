using BepInEx.Configuration;

namespace JmtLiftoffMod.Features.Competition;

internal sealed class CompetitionConfig
{
    public ConfigEntry<bool>   Enabled            { get; }
    public ConfigEntry<string> ServerUrl          { get; }
    public ConfigEntry<string> ApiKey             { get; }
    public ConfigEntry<int>    ReconnectDelaySecs { get; }

    public CompetitionConfig(ConfigFile config)
    {
        const string section = "Competition";

        // Reconnect/keep-alive tuning is fixed and routed through an in-memory config so it
        // never appears in the plugin's .cfg file — only the connection essentials are visible.
        var hidden = HiddenConfig.Create();

        ApiKey = config.Bind(section, "ApiKey", "",
            "API key sent in the Authorization header when connecting. Issued by the competition server for this bot.");

        Enabled = config.Bind(section, "Enabled", true,
            "Enable the competition server connection.");

        ServerUrl = config.Bind(section, "ServerUrl", "ws://localhost:3000/ws/plugin",
            "WebSocket URL of the competition server.");

        ReconnectDelaySecs = hidden.Bind(section, "ReconnectDelaySecs", 5,
            new ConfigDescription("Seconds to wait before reconnecting after a dropped connection.", new AcceptableValueRange<int>(1, 60)));

        KeepAliveIntervalSecs = hidden.Bind(section, "KeepAliveIntervalSecs", 60,
            new ConfigDescription("Seconds between keep-alive events sent to the server. Prevents idle-kick when the bot is in the lobby waiting room.", new AcceptableValueRange<int>(10, 300)));
    }

    public ConfigEntry<int> KeepAliveIntervalSecs { get; }
}

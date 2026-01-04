using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using DeathBuffTracker.Models;

namespace DeathBuffTracker;

[Serializable]
public sealed class Configuration : IPluginConfiguration {
    [NonSerialized]
    private IDalamudPluginInterface pluginInterface = null!;

    public int Version { get; set; } = 1;

    public bool TrackDeaths { get; set; } = true;
    public bool TrackStatusGains { get; set; } = true;
    public bool TrackOnlyInInstances { get; set; } = true;
    public bool TrackSelf { get; set; } = true;
    public bool TrackPartyMembers { get; set; } = true;
    public bool TrackOtherPlayers { get; set; } = false;

    public List<TrackedStatus> TrackedStatuses { get; set; } = new();

    public static Configuration Get(IDalamudPluginInterface pluginInterface) {
        var config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.pluginInterface = pluginInterface;
        return config;
    }

    public void Save() {
        pluginInterface.SavePluginConfig(this);
    }
}

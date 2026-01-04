using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin;
using DeathBuffTracker.Models;

namespace DeathBuffTracker.Storage;

public sealed class TrackedStatusStore {
    private readonly string filePath;
    private readonly JsonSerializerOptions options = new() {
        WriteIndented = true,
    };

    public bool FileExists => File.Exists(filePath);

    public TrackedStatusStore(IDalamudPluginInterface pluginInterface) {
        var directory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "tracked-statuses.json");
    }

    public List<TrackedStatus>? Load() {
        if (!File.Exists(filePath)) {
            return null;
        }

        try {
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) {
                return new List<TrackedStatus>();
            }

            return JsonSerializer.Deserialize<List<TrackedStatus>>(json, options) ?? new List<TrackedStatus>();
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to load tracked status store");
            return null;
        }
    }

    public void Save(IEnumerable<TrackedStatus> statuses) {
        try {
            var json = JsonSerializer.Serialize(statuses, options);
            File.WriteAllText(filePath, json);
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to save tracked status store");
        }
    }
}

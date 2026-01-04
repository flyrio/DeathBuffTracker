using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin;
using DeathBuffTracker.Models;

namespace DeathBuffTracker.Storage;

public sealed class JsonEventStore {
    private const int CurrentSchemaVersion = 1;
    private readonly string filePath;
    private readonly object gate = new();
    private readonly List<DeathBuffEventRecord> records = new();
    private readonly JsonSerializerOptions options = new() {
        WriteIndented = true,
    };

    public JsonEventStore(IDalamudPluginInterface pluginInterface) {
        var directory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "death-buff-tracker.json");
    }

    public IReadOnlyList<DeathBuffEventRecord> Records {
        get {
            lock (gate) {
                return records.AsReadOnly();
            }
        }
    }

    public List<DeathBuffEventRecord> GetSnapshot() {
        lock (gate) {
            return new List<DeathBuffEventRecord>(records);
        }
    }

    public void Load() {
        if (!File.Exists(filePath)) {
            return;
        }

        try {
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) {
                return;
            }

            using var document = JsonDocument.Parse(json);
            List<DeathBuffEventRecord>? loaded = null;

            if (document.RootElement.ValueKind == JsonValueKind.Array) {
                loaded = JsonSerializer.Deserialize<List<DeathBuffEventRecord>>(json, options);
            } else if (document.RootElement.ValueKind == JsonValueKind.Object) {
                var envelope = JsonSerializer.Deserialize<EventStoreEnvelope>(json, options);
                if (envelope != null) {
                    loaded = envelope.Records;
                    if (envelope.SchemaVersion != CurrentSchemaVersion) {
                        Service.PluginLog.Warning($"Event store schema version {envelope.SchemaVersion} loaded; current is {CurrentSchemaVersion}.");
                    }
                }
            }

            if (loaded == null) {
                return;
            }

            lock (gate) {
                records.Clear();
                records.AddRange(loaded);
            }
        } catch (JsonException ex) {
            BackupCorruptFile(ex);
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to load event store");
        }
    }

    public void Add(DeathBuffEventRecord record) {
        lock (gate) {
            records.Add(record);
        }

        Save();
    }

    public void Save() {
        try {
            List<DeathBuffEventRecord> snapshot;
            lock (gate) {
                snapshot = new List<DeathBuffEventRecord>(records);
            }

            var envelope = new EventStoreEnvelope {
                SchemaVersion = CurrentSchemaVersion,
                Records = snapshot,
            };

            var json = JsonSerializer.Serialize(envelope, options);
            var tempPath = $"{filePath}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, true);
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to save event store");
        }
    }

    private void BackupCorruptFile(Exception ex) {
        Service.PluginLog.Error(ex, "Failed to load event store");
        try {
            var backupPath = $"{filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.json";
            File.Copy(filePath, backupPath, true);
            Service.PluginLog.Warning($"Backed up corrupt event store to {backupPath}");
        } catch (Exception backupEx) {
            Service.PluginLog.Error(backupEx, "Failed to backup corrupt event store");
        }
    }

    private sealed class EventStoreEnvelope {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<DeathBuffEventRecord> Records { get; set; } = new();
    }
}

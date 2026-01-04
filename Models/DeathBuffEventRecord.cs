using System;

namespace DeathBuffTracker.Models;

public sealed class DeathBuffEventRecord {
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public uint? ContentId { get; init; }
    public string? ContentName { get; init; }

    public string PlayerName { get; init; } = string.Empty;
    public uint? PlayerHomeWorldId { get; init; }
    public string? PlayerHomeWorldName { get; init; }
    public uint? PlayerCurrentWorldId { get; init; }
    public string? PlayerCurrentWorldName { get; init; }
    public TrackerEventType EventType { get; init; }

    public uint? StatusId { get; init; }
    public string? StatusName { get; init; }
    public uint? StatusStackCount { get; init; }
    public float? StatusDurationSeconds { get; init; }

    public ulong? DamageSourceId { get; init; }
    public string? DamageSourceName { get; init; }
    public uint? DamageActionId { get; init; }
    public string? DamageActionName { get; init; }
    public string? DamageType { get; init; }
}

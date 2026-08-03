using CznTranslator.Core.Config;

namespace CznTranslator.Detect;

public enum ZoneState
{
    /// <summary>Hash matches the reference within the threshold and the zone was already handled.</summary>
    Idle,

    /// <summary>Hash moved — the settle timer restarted, OCR must not run yet.</summary>
    Dirty,

    /// <summary>Hash has held still for settleMs — ready for the cache probe and then OCR.</summary>
    Settled
}

public sealed record ZoneObservation(string ZoneId, ZoneState State, ulong Hash, int Distance);

/// <summary>
/// Per-zone change detection with a settle timer (TZ §3, steps 1–3).
/// <para>
/// The reference hash is deliberately <em>not</em> refreshed while the distance stays under the
/// threshold. Refreshing it every frame would let a slow fade drift arbitrarily far without ever
/// tripping the detector, which is exactly the fade-in failure mode §11 warns about.
/// </para>
/// </summary>
public sealed class ZoneChangeDetector(int settleMs, int hashDistanceThreshold = 4)
{
    private sealed class ZoneStateEntry
    {
        public ulong ReferenceHash;
        public long ChangedAtMs;
        public bool HasReference;
        public bool SettleReported;
    }

    private readonly Dictionary<string, ZoneStateEntry> _zones = new(StringComparer.Ordinal);

    public int SettleMs { get; set; } = settleMs > 0
        ? settleMs
        : throw new ArgumentOutOfRangeException(nameof(settleMs), "settleMs must be positive.");

    public int HashDistanceThreshold { get; set; } = hashDistanceThreshold;

    /// <summary>
    /// Feeds one hash observation. <paramref name="nowMs"/> is a monotonic millisecond clock
    /// (<see cref="Environment.TickCount64"/> in production, a fake in tests).
    /// </summary>
    public ZoneObservation Observe(string zoneId, ulong hash, long nowMs)
    {
        ArgumentException.ThrowIfNullOrEmpty(zoneId);

        if (!_zones.TryGetValue(zoneId, out var entry))
        {
            entry = new ZoneStateEntry();
            _zones[zoneId] = entry;
        }

        if (!entry.HasReference)
        {
            entry.HasReference = true;
            entry.ReferenceHash = hash;
            entry.ChangedAtMs = nowMs;
            entry.SettleReported = false;
            return new ZoneObservation(zoneId, ZoneState.Dirty, hash, Distance: 64);
        }

        var distance = PerceptualHash.Hamming(hash, entry.ReferenceHash);

        if (distance > HashDistanceThreshold)
        {
            entry.ReferenceHash = hash;
            entry.ChangedAtMs = nowMs;
            entry.SettleReported = false;
            return new ZoneObservation(zoneId, ZoneState.Dirty, hash, distance);
        }

        if (entry.SettleReported)
            return new ZoneObservation(zoneId, ZoneState.Idle, entry.ReferenceHash, distance);

        if (nowMs - entry.ChangedAtMs < SettleMs)
            return new ZoneObservation(zoneId, ZoneState.Dirty, hash, distance);

        entry.SettleReported = true;
        return new ZoneObservation(zoneId, ZoneState.Settled, entry.ReferenceHash, distance);
    }

    /// <summary>Forces the next observation of this zone to be treated as a change.</summary>
    public void Invalidate(string zoneId) => _zones.Remove(zoneId);

    public void InvalidateAll() => _zones.Clear();

    /// <summary>
    /// Zones in <see cref="ZoneMode.Auto"/> above <c>autoZoneMaxPriority</c> are demoted to manual —
    /// that is how the laptop profile restricts automatic polling to the priority-0 zone.
    /// </summary>
    public static ZoneMode EffectiveMode(ZoneConfig zone, DetectSection detect) =>
        zone.Mode == ZoneMode.Auto && zone.Priority > detect.AutoZoneMaxPriority
            ? ZoneMode.Manual
            : zone.Mode;

    public static bool IsPolledAutomatically(ZoneConfig zone, DetectSection detect) =>
        EffectiveMode(zone, detect) != ZoneMode.Manual;
}

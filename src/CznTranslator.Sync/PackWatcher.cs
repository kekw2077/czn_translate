using System.Diagnostics;
using System.Security.Cryptography;
using CznTranslator.Core.Abstractions;
using Serilog;

namespace CznTranslator.Sync;

public enum PackCheckOutcome
{
    /// <summary>MD5 matches the newest row in <c>pack_versions</c>.</summary>
    UpToDate,

    /// <summary>The pack changed — a patch shipped and the base needs rebuilding.</summary>
    PatchDetected,

    /// <summary>No versions recorded yet: the conveyor has never run against this install.</summary>
    NeverImported,

    /// <summary>The pack was not found at the configured path.</summary>
    PackMissing,

    /// <summary>The game is running, so the file is not safe to read.</summary>
    GameRunning
}

public sealed record PackCheckResult(PackCheckOutcome Outcome, string? Md5, PackVersion? Known, string Message);

/// <summary>
/// Startup check from TZ §7: hash <c>data.pack</c> and compare it with the last recorded version.
/// <para>
/// Read-only, and only while the game is closed — that is the one contact this application has
/// with anything belonging to the game, and it stays a plain file read. Nothing is rebuilt
/// automatically; the user is told a patch landed and starts the conveyor themselves.
/// </para>
/// </summary>
public sealed class PackWatcher(IPackVersionStore versionStore, ILogger? log = null)
{
    private readonly IPackVersionStore _versionStore = versionStore ?? throw new ArgumentNullException(nameof(versionStore));
    private readonly ILogger _log = log ?? Log.Logger;

    public async Task<PackCheckResult> CheckAsync(
        string packPath,
        string gameProcessName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packPath))
        {
            return new PackCheckResult(
                PackCheckOutcome.PackMissing, null, null,
                "sync.packPath is not configured, patch detection is off.");
        }

        if (IsGameRunning(gameProcessName))
        {
            return new PackCheckResult(
                PackCheckOutcome.GameRunning, null, null,
                $"{gameProcessName} is running — data.pack is only read with the game closed.");
        }

        if (!File.Exists(packPath))
        {
            return new PackCheckResult(
                PackCheckOutcome.PackMissing, null, null,
                $"data.pack not found at '{packPath}'.");
        }

        var md5 = await ComputeMd5Async(packPath, cancellationToken).ConfigureAwait(false);
        var known = await _versionStore.GetLatestAsync(cancellationToken).ConfigureAwait(false);

        if (known is null)
        {
            return new PackCheckResult(
                PackCheckOutcome.NeverImported, md5, null,
                "No pack version recorded yet — run tools/import_dump.py to build the base.");
        }

        if (string.Equals(known.PackMd5, md5, StringComparison.OrdinalIgnoreCase))
        {
            _log.Debug("data.pack matches recorded version {Version}.", known.Version);
            return new PackCheckResult(PackCheckOutcome.UpToDate, md5, known, $"Base matches pack version {known.Version}.");
        }

        return new PackCheckResult(
            PackCheckOutcome.PatchDetected, md5, known,
            $"data.pack no longer matches version {known.Version}: a patch shipped and the base needs rebuilding.");
    }

    /// <summary>
    /// Checks by process name only. Nothing here opens a handle to the game — no OpenProcess, no
    /// module enumeration — because that is exactly the kind of contact §0 rules out.
    /// </summary>
    public static bool IsGameRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Streamed so a multi-gigabyte pack does not land in memory. It still takes seconds, which is
    /// why this runs once at start-up and not on a timer.
    /// </summary>
    public static async Task<string> ComputeMd5Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            useAsync: true);

        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

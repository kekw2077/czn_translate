namespace CznTranslator.Core.Abstractions;

public sealed record PackVersion(int Version, string PackMd5, DateTimeOffset? RippedAt, string? Note);

/// <summary>The <c>pack_versions</c> table (TZ §5, §7). Implemented in CznTranslator.Lookup.</summary>
public interface IPackVersionStore
{
    Task<PackVersion?> GetLatestAsync(CancellationToken cancellationToken = default);

    Task<int> RecordAsync(string packMd5, string? note, CancellationToken cancellationToken = default);
}

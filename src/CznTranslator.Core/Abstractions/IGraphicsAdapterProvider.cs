namespace CznTranslator.Core.Abstractions;

/// <summary>One DXGI adapter as seen by <c>EnumAdapters1</c>.</summary>
public sealed record GraphicsAdapter(
    int Index,
    string Description,
    ulong DedicatedVideoMemory,
    bool IsSoftware);

/// <summary>
/// Adapter enumeration, abstracted so the §4 provider cascade can be exercised without a GPU.
/// The real implementation (DXGI) lives in CznTranslator.Capture, which is Windows-only.
/// </summary>
public interface IGraphicsAdapterProvider
{
    IReadOnlyList<GraphicsAdapter> Enumerate();
}

/// <summary>Used on machines with no DXGI at all — the cascade then falls through to CPU.</summary>
public sealed class EmptyAdapterProvider : IGraphicsAdapterProvider
{
    public IReadOnlyList<GraphicsAdapter> Enumerate() => [];
}

/// <summary>Physical CPU topology for the CPU execution provider's thread pool (TZ §12).</summary>
public interface ICpuTopology
{
    /// <summary>Physical cores, hyperthreading siblings not counted.</summary>
    int PhysicalCores { get; }

    /// <summary>
    /// Performance cores only. On Intel 12th gen and newer <c>NumberOfCores</c> lumps P and E
    /// cores together, and an ORT thread pool syncs to its slowest member, so handing it the
    /// E-cores drags the whole inference down.
    /// </summary>
    int PerformanceCores { get; }

    /// <summary>True when the CPU has more than one core efficiency class.</summary>
    bool IsHybrid { get; }
}

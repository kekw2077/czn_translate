using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Core.Models;

namespace CznTranslator.Ocr;

public sealed record BackendDecision(OcrProviderKind Kind, GraphicsAdapter? Adapter, string Reason);

/// <summary>
/// The §4 provider cascade, kept free of DXGI so it can be exercised against a made-up adapter
/// list: enumerate → drop software adapters → sort by dedicated VRAM → take the first → fall back
/// to CPU when nothing suitable is left.
/// </summary>
public static class AdapterSelection
{
    // Below this much dedicated VRAM an adapter is treated as integrated. Integrated GPUs report a
    // few hundred MB; discrete cards report gigabytes, so 1 GiB cleanly separates the two.
    private const ulong IntegratedVramCeiling = 1UL * 1024 * 1024 * 1024;

    public static BackendDecision Decide(OcrSection settings, IReadOnlyList<GraphicsAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(adapters);

        if (settings.Provider == OcrProviderKind.Cpu)
            return new BackendDecision(OcrProviderKind.Cpu, null, "ocr.provider is pinned to cpu.");

        if (settings.AdapterIndex >= 0)
        {
            var forced = adapters.FirstOrDefault(a => a.Index == settings.AdapterIndex);
            if (forced is not null)
            {
                return new BackendDecision(
                    OcrProviderKind.DirectMl,
                    forced,
                    $"ocr.adapterIndex pinned adapter #{forced.Index} ({forced.Description}).");
            }

            if (settings.Provider == OcrProviderKind.DirectMl)
            {
                throw new InvalidOperationException(
                    $"ocr.adapterIndex={settings.AdapterIndex} does not exist and ocr.provider is pinned to dml. " +
                    $"Adapters present: {Describe(adapters)}.");
            }

            // In auto mode a stale pinned index is a config leftover, not a reason to refuse to start.
            return DecideAutomatically(
                settings,
                adapters,
                $"ocr.adapterIndex={settings.AdapterIndex} does not exist, falling back to automatic selection. ");
        }

        return DecideAutomatically(settings, adapters, string.Empty);
    }

    private static BackendDecision DecideAutomatically(
        OcrSection settings,
        IReadOnlyList<GraphicsAdapter> adapters,
        string prefix)
    {
        // WARP would technically accept the DML provider and then run the model on the CPU
        // through a graphics driver — strictly slower than the CPU execution provider.
        var usable = adapters
            .Where(a => !a.IsSoftware)
            .OrderByDescending(a => a.DedicatedVideoMemory)
            .ThenBy(a => a.Index)
            .ToList();

        if (usable.Count > 0)
        {
            var chosen = usable[0];

            // Integrated GPUs report a tiny dedicated pool. For this small OCR model DirectML on
            // one is actually SLOWER than the CPU execution provider (measured ~3x on Iris Xe),
            // and it steals the very GPU the game renders on — the overlay would stutter the game.
            // A discrete card (gigabytes dedicated) wins on DirectML, so only steer to CPU when the
            // best adapter is clearly integrated and the choice is ours (auto, not a pinned dml).
            if (settings.Provider == OcrProviderKind.Auto && chosen.DedicatedVideoMemory < IntegratedVramCeiling)
            {
                return new BackendDecision(
                    OcrProviderKind.Cpu,
                    null,
                    $"{prefix}Best adapter #{chosen.Index} ({chosen.Description}) is integrated " +
                    $"({chosen.DedicatedVideoMemory / (1024 * 1024)} MB dedicated); the CPU provider is faster for OCR " +
                    "here and leaves the GPU to the game.");
            }

            return new BackendDecision(
                OcrProviderKind.DirectMl,
                chosen,
                $"{prefix}DirectML on adapter #{chosen.Index} ({chosen.Description}, " +
                $"{chosen.DedicatedVideoMemory / (1024 * 1024)} MB dedicated).");
        }

        if (settings.Provider == OcrProviderKind.DirectMl)
        {
            throw new InvalidOperationException(
                "ocr.provider is pinned to dml but no non-software DXGI adapter is available. " +
                $"Adapters present: {Describe(adapters)}.");
        }

        return new BackendDecision(
            OcrProviderKind.Cpu,
            null,
            $"{prefix}No usable DXGI adapter, falling back to the CPU execution provider.");
    }

    private static string Describe(IReadOnlyList<GraphicsAdapter> adapters) =>
        adapters.Count == 0
            ? "none"
            : string.Join(", ", adapters.Select(a => $"#{a.Index} {a.Description}{(a.IsSoftware ? " (software)" : "")}"));
}

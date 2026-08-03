using CznTranslator.Core.Models;

namespace CznTranslator.Core.Abstractions;

/// <summary>
/// Text recognition over a single zone ROI. Two implementations (TZ §4): <c>DmlOcrBackend</c>
/// and <c>CpuOcrBackend</c>. They differ in session options, not in pre/post-processing, so
/// everything except the session setup is shared.
/// </summary>
public interface IOcrBackend : IDisposable
{
    OcrBackendInfo Info { get; }

    /// <summary>
    /// Runs both models on empty inputs so the 1–3 s DirectML shader compilation happens at
    /// startup instead of inside the first real translation (TZ §4, §11).
    /// </summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects and recognizes lines in the ROI. Boxes in the result are in ROI-local pixels.
    /// </summary>
    Task<OcrResult> RecognizeAsync(GrayImage roi, OcrRequestOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Per-request knobs. The zone mode decides whether detection runs at all.</summary>
public sealed record OcrRequestOptions(
    bool SingleLine = false,
    bool WholeRoiAsOneBlock = false)
{
    public static readonly OcrRequestOptions Default = new();
}

/// <summary>Picks the execution provider by the §4 cascade and reports what it settled on.</summary>
public interface IOcrBackendFactory
{
    IOcrBackend Create(Config.AppConfig config);
}

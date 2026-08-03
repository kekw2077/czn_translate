using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Serilog;

namespace CznTranslator.Ocr;

/// <summary>
/// Builds the backend by the §4 cascade and reports what it settled on — the chosen provider and
/// adapter name go to the log and into the tray tooltip, so a silent fall back to CPU is visible
/// rather than merely slow.
/// </summary>
public sealed class OcrBackendFactory(
    IGraphicsAdapterProvider adapterProvider,
    ICpuTopology? cpuTopology = null,
    ILogger? log = null) : IOcrBackendFactory
{
    private readonly IGraphicsAdapterProvider _adapterProvider =
        adapterProvider ?? throw new ArgumentNullException(nameof(adapterProvider));

    private readonly ICpuTopology _cpuTopology = cpuTopology ?? CpuTopology.Detect();
    private readonly ILogger _log = log ?? Log.Logger;

    public IOcrBackend Create(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var models = OcrModelSet.Resolve(config.Ocr);
        var decision = Decide(config.Ocr);

        _log.Information("OCR backend: {Reason}", decision.Reason);

        if (decision.Kind == OcrProviderKind.DirectMl && decision.Adapter is { } adapter)
        {
            try
            {
                return CreateDirectMl(models, config.Ocr, adapter);
            }
            catch (Exception ex) when (config.Ocr.Provider == OcrProviderKind.Auto)
            {
                // The adapter enumerated fine but the provider refused to initialize — an old
                // driver, or DirectML missing from the system. In auto mode that is a reason to
                // degrade, not to refuse to start.
                _log.Warning(ex, "DirectML failed to initialize on {Adapter}, falling back to the CPU provider.", adapter.Description);
            }
        }

        return CreateCpu(models, config.Ocr);
    }

    public BackendDecision Decide(OcrSection settings) =>
        AdapterSelection.Decide(settings, _adapterProvider.Enumerate());

    private OnnxOcrBackend CreateDirectMl(OcrModelSet models, OcrSection settings, GraphicsAdapter adapter)
    {
        var info = new OcrBackendInfo(OcrProviderKind.DirectMl, adapter.Description, adapter.Index, settings.Quantized);

        // Separate SessionOptions per session: ORT takes ownership of the native handle, so
        // sharing one instance across two sessions is a use-after-free waiting to happen.
        return new OnnxOcrBackend(
            models,
            OcrSessionOptions.ForDirectMl(adapter.Index),
            OcrSessionOptions.ForDirectMl(adapter.Index),
            settings,
            info,
            _log);
    }

    private OnnxOcrBackend CreateCpu(OcrModelSet models, OcrSection settings)
    {
        _log.Information(
            "CPU provider: {Performance} performance core(s) of {Physical} physical{Hybrid}.",
            _cpuTopology.PerformanceCores,
            _cpuTopology.PhysicalCores,
            _cpuTopology.IsHybrid ? ", hybrid P/E topology" : string.Empty);

        var info = new OcrBackendInfo(OcrProviderKind.Cpu, "CPU", -1, settings.Quantized);

        return new OnnxOcrBackend(
            models,
            OcrSessionOptions.ForCpu(_cpuTopology),
            OcrSessionOptions.ForCpu(_cpuTopology),
            settings,
            info,
            _log);
    }
}

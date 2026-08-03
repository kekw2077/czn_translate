using CznTranslator.Core.Abstractions;
using Microsoft.ML.OnnxRuntime;

namespace CznTranslator.Ocr;

/// <summary>
/// Session options for the two backends. The two sets are opposites — DirectML needs the memory
/// pattern off and sequential execution, the CPU provider wants both on — so they cannot share a
/// single setup path (TZ §4, §12).
/// </summary>
public static class OcrSessionOptions
{
    public static SessionOptions ForDirectMl(int deviceId)
    {
        var options = new SessionOptions();

        // Both of these are required by the DML execution provider, not preferences.
        options.EnableMemoryPattern = false;
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        options.AppendExecutionProvider_DML(deviceId);

        return options;
    }

    /// <summary>
    /// CPU provider. It is compiled into the same DirectML build and is selected simply by not
    /// appending the DML provider — adding the plain Microsoft.ML.OnnxRuntime package to get it
    /// would collide on the native DLLs.
    /// </summary>
    public static SessionOptions ForCpu(ICpuTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        var options = new SessionOptions();
        options.EnableMemoryPattern = true;
        options.ExecutionMode = ExecutionMode.ORT_PARALLEL;
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        // Physical cores, never logical: handing ORT the hyperthreading siblings measures worse
        // on every benchmark. On a hybrid CPU only the P-cores count — the pool syncs to its
        // slowest member, so an E-core in the pool sets the pace for the whole inference.
        options.IntraOpNumThreads = Math.Max(1, topology.PerformanceCores);
        options.InterOpNumThreads = 2;

        if (topology.IsHybrid)
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

        return options;
    }
}

namespace CznTranslator.Core.Models;

/// <summary>One recognised line: text, mean per-character CTC confidence, box in zone-local pixels.</summary>
public sealed record OcrLine(string Text, double Confidence, PixelRect Box);

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, double DetectMs, double RecognizeMs)
{
    public static readonly OcrResult Empty = new([], 0, 0);

    public double TotalMs => DetectMs + RecognizeMs;

    /// <summary>
    /// Mean confidence weighted by line length — a single short garbage line should not
    /// drag the whole zone below the dynamic fuzzy threshold (TZ §5, шаг 3).
    /// </summary>
    public double MeanConfidence
    {
        get
        {
            var weight = 0;
            var acc = 0.0;
            foreach (var line in Lines)
            {
                var w = Math.Max(1, line.Text.Length);
                acc += line.Confidence * w;
                weight += w;
            }

            return weight == 0 ? 0 : acc / weight;
        }
    }
}

public enum OcrProviderKind
{
    Auto,
    DirectMl,
    Cpu
}

public sealed record OcrBackendInfo(OcrProviderKind Kind, string AdapterName, int AdapterIndex, bool Quantized)
{
    public override string ToString() =>
        Kind switch
        {
            OcrProviderKind.DirectMl => $"DirectML · {AdapterName} (#{AdapterIndex}){(Quantized ? " · int8" : " · fp32")}",
            OcrProviderKind.Cpu => $"CPU{(Quantized ? " · int8" : " · fp32")}",
            _ => "auto"
        };
}

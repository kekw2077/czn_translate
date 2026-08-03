using CznTranslator.Core.Config;

namespace CznTranslator.Ocr;

/// <summary>
/// Resolves the three model files (TZ §4 and the §12 profile table). The angle classifier is
/// deliberately absent: the game UI is horizontal and <c>cls</c> would cost 3–5 ms per line for
/// nothing.
/// </summary>
public sealed record OcrModelSet(string DetectionPath, string RecognitionPath, string DictionaryPath)
{
    public const string DetectionStem = "ch_PP-OCRv4_det_infer";
    public const string RecognitionStem = "en_PP-OCRv4_rec_infer";
    public const string DictionaryFile = "en_dict.txt";

    /// <summary>Builds the paths without touching the disk.</summary>
    public static OcrModelSet Resolve(OcrSection settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var suffix = settings.Quantized ? "_quant" : string.Empty;

        return new OcrModelSet(
            Path.Combine(settings.ModelsDirectory, $"{DetectionStem}{suffix}.onnx"),
            Path.Combine(settings.ModelsDirectory, $"{RecognitionStem}{suffix}.onnx"),
            Path.Combine(settings.ModelsDirectory, DictionaryFile));
    }

    /// <summary>Names every missing file at once — one error beats three consecutive startups.</summary>
    public void EnsureFilesExist()
    {
        var missing = new List<string>();

        foreach (var path in new[] { DetectionPath, RecognitionPath, DictionaryPath })
        {
            if (!File.Exists(path))
                missing.Add(path);
        }

        if (missing.Count == 0)
            return;

        throw new FileNotFoundException(
            "OCR models are missing: " + string.Join(", ", missing) +
            ". Download the RapidOCR ONNX exports into the models directory.");
    }
}

using System.Text;

namespace CznTranslator.Ocr;

public readonly record struct CtcDecodeResult(string Text, double Confidence);

/// <summary>
/// Greedy CTC decoding for the PP-OCR recognition head (TZ §4): argmax per time step, collapse
/// runs of the same class, drop the blank.
/// <para>
/// Confidence is the mean probability over the time steps that actually contributed a character.
/// Averaging over all steps instead would let a long run of confident blanks inflate the score of
/// a short, badly read line — and this number is what selects the fuzzy threshold downstream.
/// </para>
/// </summary>
public static class CtcDecoder
{
    /// <summary>
    /// Decodes one sequence. <paramref name="probabilities"/> is row-major
    /// <c>[timeSteps, classCount]</c>, already softmaxed by the model.
    /// </summary>
    public static CtcDecodeResult Decode(
        ReadOnlySpan<float> probabilities,
        int timeSteps,
        int classCount,
        CharacterDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);

        if (timeSteps <= 0 || classCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeSteps), "Sequence and class count must be positive.");

        if (probabilities.Length < (long)timeSteps * classCount)
        {
            throw new ArgumentException(
                $"Expected {(long)timeSteps * classCount} probabilities, got {probabilities.Length}.",
                nameof(probabilities));
        }

        if (classCount > dictionary.Count)
        {
            throw new ArgumentException(
                $"The model emits {classCount} classes but the dictionary holds {dictionary.Count} labels. " +
                "The rec model and en_dict.txt do not belong together.",
                nameof(dictionary));
        }

        var builder = new StringBuilder(timeSteps);
        var confidenceSum = 0.0;
        var kept = 0;
        var previousIndex = -1;

        for (var step = 0; step < timeSteps; step++)
        {
            var offset = step * classCount;

            var bestIndex = 0;
            var bestValue = probabilities[offset];
            for (var cls = 1; cls < classCount; cls++)
            {
                var value = probabilities[offset + cls];
                if (value > bestValue)
                {
                    bestValue = value;
                    bestIndex = cls;
                }
            }

            // Collapse only *consecutive* duplicates — that is what lets a genuine "ll" survive,
            // because CTC separates the two with a blank step.
            if (bestIndex != previousIndex && !dictionary.IsBlank(bestIndex))
            {
                builder.Append(dictionary[bestIndex]);
                confidenceSum += bestValue;
                kept++;
            }

            previousIndex = bestIndex;
        }

        var confidence = kept == 0 ? 0.0 : confidenceSum / kept;
        return new CtcDecodeResult(builder.ToString(), confidence);
    }

    /// <summary>Decodes a whole batch laid out as <c>[batch, timeSteps, classCount]</c>.</summary>
    public static IReadOnlyList<CtcDecodeResult> DecodeBatch(
        ReadOnlySpan<float> probabilities,
        int batchSize,
        int timeSteps,
        int classCount,
        CharacterDictionary dictionary)
    {
        var results = new List<CtcDecodeResult>(batchSize);
        var stride = timeSteps * classCount;

        for (var item = 0; item < batchSize; item++)
            results.Add(Decode(probabilities.Slice(item * stride, stride), timeSteps, classCount, dictionary));

        return results;
    }
}

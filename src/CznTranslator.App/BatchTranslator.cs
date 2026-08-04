using CznTranslator.Core.Models;
using CznTranslator.Lookup;

namespace CznTranslator.App;

public sealed record TranslationProgress(
    int Done,
    int Total,
    string Message,
    bool Finished = false,
    int Translated = 0,
    int MemoryHits = 0,
    int Skipped = 0,
    int FailedBatches = 0,
    long InputTokens = 0,
    long OutputTokens = 0);

/// <summary>
/// The C# port of tools/translate.py: for every pending (new/stale) row, reuse a human-reviewed
/// translation of the same normalized text if there is one, pass codes/numbers through unchanged,
/// then collapse identical English to one model call and fan the answer back out. Machine output
/// lands as 'mt' — the review queue. Reports progress so the settings window can show a bar.
/// </summary>
public sealed class BatchTranslator(
    StringRepository repository,
    ApiTranslationClient client,
    IReadOnlyDictionary<string, string>? glossary = null)
{
    private const int BatchSize = 40;

    private readonly StringRepository _repository = repository;
    private readonly ApiTranslationClient _client = client;
    private readonly IReadOnlyDictionary<string, string> _glossary =
        glossary ?? new Dictionary<string, string>();

    public async Task RunAsync(int? limit, IProgress<TranslationProgress> progress, CancellationToken ct)
    {
        var pending = _repository.Pending(limit);
        var total = pending.Count;
        int memoryHits = 0, skipped = 0, translated = 0, failed = 0;
        var done = 0;

        // A cold base has no reviewed rows, so the memory lookup would be 100k pointless queries;
        // check once and skip it entirely in that case.
        var memoryEnabled = _repository.CountByStatus(StringStatus.Reviewed)
                            + _repository.CountByStatus(StringStatus.Locked) > 0;

        void Report(string message, bool finished = false) => progress.Report(new TranslationProgress(
            done, total, message, finished, translated, memoryHits, skipped, failed,
            _client.InputTokens, _client.OutputTokens));

        // Phase 1 — memory reuse, pass-through, and collect what the model must see.
        var remaining = new List<StringRow>();
        foreach (var row in pending)
        {
            ct.ThrowIfCancellationRequested();

            if (!TranslationValidator.IsTranslatable(row.English))
            {
                _repository.SetTranslation(row.Id, row.English, StringStatus.Reviewed);
                skipped++;
                done++;
            }
            else if (memoryEnabled && _repository.FindTranslationMemory(row.Norm) is { } reused)
            {
                _repository.SetTranslation(row.Id, reused, StringStatus.Reviewed);
                memoryHits++;
                done++;
            }
            else
            {
                remaining.Add(row);
            }

            if (done % 500 == 0 && done > 0)
                Report($"Память/пропуск: {done:N0} из {total:N0}…");
        }

        // Phase 2 — collapse duplicate English so it is translated once.
        var byEn = new Dictionary<string, List<StringRow>>(StringComparer.Ordinal);
        foreach (var row in remaining)
        {
            if (!byEn.TryGetValue(row.English, out var list))
                byEn[row.English] = list = [];
            list.Add(row);
        }

        var items = new List<TranslationItem>();
        var idToEn = new Dictionary<int, string>();
        var nextId = 1;
        foreach (var en in byEn.Keys)
        {
            items.Add(new TranslationItem(nextId, en));
            idToEn[nextId] = en;
            nextId++;
        }

        Report($"Уникальных строк для модели: {items.Count:N0} (из {remaining.Count:N0}).");

        // Phase 3 — batch translate the unique strings, fan each answer back to its rows.
        for (var start = 0; start < items.Count; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = items.GetRange(start, Math.Min(BatchSize, items.Count - start));

            Dictionary<int, string> results;
            try
            {
                results = await _client.TranslateBatchAsync(batch, _glossary, ct);
            }
            catch (BatchTranslationException ex)
            {
                // Leave these rows as-is so the next run retries them, rather than writing partial
                // output that looks the same as an untranslated string three steps later.
                failed++;
                Report($"Батч не прошёл: {ex.Message}");
                continue;
            }

            foreach (var (id, russian) in results)
            {
                foreach (var row in byEn[idToEn[id]])
                {
                    _repository.SetTranslation(row.Id, russian, StringStatus.MachineTranslated);
                    translated++;
                    done++;
                }
            }

            Report($"Переведено {translated:N0}, память {memoryHits:N0}, пропущено {skipped:N0}…");
        }

        Report(
            failed > 0
                ? $"Готово с ошибками: {failed} батч(ей) не прошли, повторите запуск."
                : "Готово.",
            finished: true);
    }
}

using CznTranslator.Core.Models;
using CznTranslator.Lookup;

namespace CznTranslator.App;

public sealed record PackDiff(int New, int Changed, int Removed, int Unchanged);

/// <summary>
/// The C# port of tools/diff_pack.py --pairs: decode the current pack, compare its {key → English}
/// against the base, and (when applying) queue new strings, mark changed ones stale while keeping
/// their old ru, and leave removed rows in place. Nothing is deleted (§8).
/// </summary>
public sealed class PackUpdater(StringRepository repository, PackExtractor extractor)
{
    private readonly StringRepository _repository = repository;
    private readonly PackExtractor _extractor = extractor;

    public async Task<PackDiff> RunAsync(string packPath, bool apply, IProgress<string> progress, CancellationToken ct)
    {
        var decoded = await Task.Run(() => _extractor.Extract(packPath, "en", progress, ct), ct);

        // Match import_pairs / diff_pack: a blank value is not a string to translate.
        var current = decoded
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var existing = _repository.PackKeyEnglish();

        var newKeys = new List<string>();
        var changedKeys = new List<string>();
        var unchanged = 0;
        foreach (var (key, english) in current)
        {
            if (!existing.TryGetValue(key, out var was))
                newKeys.Add(key);
            else if (was != english)
                changedKeys.Add(key);
            else
                unchanged++;
        }
        var removed = existing.Keys.Count(key => !current.ContainsKey(key));

        var diff = new PackDiff(newKeys.Count, changedKeys.Count, removed, unchanged);
        progress.Report($"Сравнение: новых {diff.New}, изменено {diff.Changed}, удалено {diff.Removed}, без изменений {diff.Unchanged}.");

        if (!apply)
        {
            progress.Report("Проверка завершена, база не изменена.");
            return diff;
        }

        ct.ThrowIfCancellationRequested();
        var version = _repository.RecordPackVersion("unknown", "native update");

        foreach (var key in newKeys)
            _repository.Upsert(current[key], null, key, "text/en", StringStatus.New, StringSource.Pack, version);

        // ru left alone (COALESCE keeps it) — the old translation shows until the re-translation lands.
        foreach (var key in changedKeys)
            _repository.Upsert(current[key], null, key, "text/en", StringStatus.Stale, StringSource.Pack, version);

        progress.Report($"Применено как версия пака {version}. Новые и устаревшие строки — во вкладке «Перевод».");
        return diff;
    }
}

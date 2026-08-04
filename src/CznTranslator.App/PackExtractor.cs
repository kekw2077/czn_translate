using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CznTranslator.App;

/// <summary>
/// The C# port of extracted/scripts/extract_pack.py: decodes the game's data.pack into a
/// {key → English} map. Two XOR layers — the pack stream (129-byte key at the global offset) and
/// each text.db blob (256-byte key at the rotation that spells 'PLPcK'). The keys are format
/// constants, not game content; they are read from pack.keys.json next to the exe rather than
/// compiled in, so the committed source stays key-free and the feature is simply absent if the
/// file is not there.
/// </summary>
public sealed class PackExtractor
{
    private static readonly byte[] Plpck = "PLPcK"u8.ToArray();

    private readonly byte[] _packKey;
    private readonly byte[] _dbKey;

    public PackExtractor(byte[] packKey, byte[] dbKey)
    {
        _packKey = packKey;
        _dbKey = dbKey;
    }

    public static string KeysPath => Path.Combine(AppContext.BaseDirectory, "pack.keys.json");

    /// <summary>Loads the extractor from pack.keys.json next to the exe, or reports why it cannot.</summary>
    public static bool TryLoadDefault(out PackExtractor? extractor, out string? error)
    {
        extractor = null;
        error = null;
        try
        {
            if (!File.Exists(KeysPath))
            {
                error = $"pack.keys.json не найден рядом с приложением ({KeysPath}).";
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(KeysPath));
            var packKey = Convert.FromHexString(doc.RootElement.GetProperty("packKey").GetString()!);
            var dbKey = Convert.FromHexString(doc.RootElement.GetProperty("dbKey").GetString()!);
            if (dbKey.Length != 256)
            {
                error = $"dbKey должен быть 256 байт, а не {dbKey.Length}.";
                return false;
            }
            extractor = new PackExtractor(packKey, dbKey);
            return true;
        }
        catch (Exception ex)
        {
            error = $"не удалось прочитать pack.keys.json: {ex.Message}";
            return false;
        }
    }

    /// <summary>data.pack (+ its ~1..~N chunks) → {key → English}. Read-only over the pack.</summary>
    public Dictionary<string, string> Extract(string packPath, string lang, IProgress<string>? progress, CancellationToken ct)
    {
        using var reader = new PackReader(packPath, _packKey);

        var head = reader.Decode(0, 16);
        if (!head.AsSpan(0, 5).SequenceEqual(Plpck))
            throw new InvalidDataException($"заголовок пака {Encoding.Latin1.GetString(head, 0, 5)}, а не PLPcK — неверный ключ или файл.");

        var want = Encoding.UTF8.GetBytes($"text/{lang}/text.db");
        var needle = "text/"u8.ToArray();
        const int block = 64 << 20;
        const int overlap = 64;
        var seen = new HashSet<long>();

        long g = 0;
        var reported = 0L;
        while (g < reader.Total)
        {
            ct.ThrowIfCancellationRequested();
            var n = (int)Math.Min(block, reader.Total - g);
            var dec = reader.Decode(g, n);
            var span = dec.AsSpan(0, n);

            var from = 0;
            while (true)
            {
                var rel = span[from..].IndexOf(needle);
                if (rel < 0)
                    break;
                var j = from + rel;
                from = j + 1;

                var p = g + j;
                if (p < 15 || !seen.Add(p))
                    continue;

                var hdr = reader.Decode(p - 15, 15);
                if (hdr[4] != 0x02)
                    continue;
                var container = U32(hdr, 0);
                int pathLen = hdr[5];
                var dataLen = U32(hdr, 6);
                if (pathLen == 0 || pathLen > 256 || container != pathLen + dataLen + 19)
                    continue;

                var pathBytes = reader.Decode(p, pathLen);
                if (!pathBytes.AsSpan().SequenceEqual(want))
                    continue;

                progress?.Report($"Найден text/{lang}/text.db ({dataLen:N0} байт), декодирую…");
                var blob = reader.Decode(p + pathLen, (int)dataLen);
                return ParsePairs(DecodeDb(blob));
            }

            if (g - reported >= (512L << 20))
            {
                reported = g;
                progress?.Report($"Сканирование пака: {g / (double)(1 << 30):F1} ГиБ…");
            }
            g += n - overlap;
        }

        throw new InvalidDataException($"text/{lang}/text.db не найден в паке.");
    }

    // ------------------------------------------------------------- layer 2: db

    private byte[] DecodeDb(byte[] blob)
    {
        for (var rot = 0; rot < 256; rot++)
        {
            var ok = true;
            for (var j = 0; j < 5; j++)
            {
                if ((blob[j] ^ _dbKey[(j + rot) % 256]) != Plpck[j])
                {
                    ok = false;
                    break;
                }
            }
            if (!ok)
                continue;

            var outBuf = new byte[blob.Length];
            for (var j = 0; j < blob.Length; j++)
                outBuf[j] = (byte)(blob[j] ^ _dbKey[(j + rot) % 256]);
            return outBuf;
        }
        throw new InvalidDataException("ни одна ротация ключа не даёт магию PLPcK в text.db.");
    }

    private static Dictionary<string, string> ParsePairs(byte[] buf)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (true)
        {
            var rel = buf.AsSpan(i).IndexOf((byte)0x02);
            if (rel < 0)
                break;
            var j = i + rel;
            if (j < 4)
            {
                i = j + 1;
                continue;
            }
            i = j + 1;

            var container = U32(buf, j - 4);
            int nameLen = buf[j + 1];
            var dataLen = U32(buf, j + 2);
            if (nameLen == 0 || container != nameLen + dataLen + 15)
                continue;

            var nameOff = j + 11;
            if (nameOff + nameLen + (long)dataLen > buf.Length)
                continue;

            var name = buf.AsSpan(nameOff, nameLen);
            var data = buf.AsSpan(nameOff + nameLen, (int)dataLen);
            i = nameOff + nameLen + (int)dataLen;

            if (name[0] == 0x09)
                continue;

            string key;
            try
            {
                key = Encoding.UTF8.GetString(name);
            }
            catch (Exception)
            {
                continue;
            }

            // data is <key>\0<text>\0 — field [1] is the localized string.
            var nul = data.IndexOf((byte)0x00);
            var text = nul >= 0 && nul + 1 <= data.Length
                ? Encoding.UTF8.GetString(data[(nul + 1)..].TrimEnd((byte)0x00))
                : Encoding.UTF8.GetString(data.TrimEnd((byte)0x00));
            pairs[key] = text;
        }
        return pairs;
    }

    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));

    // ------------------------------------------------------------ pack reader

    private sealed class PackReader : IDisposable
    {
        private readonly FileStream[] _parts;
        private readonly long[] _starts;
        private readonly byte[] _key;

        public long Total { get; }

        public PackReader(string packPath, byte[] key)
        {
            _key = key;
            var paths = new List<string> { packPath };
            for (var i = 1; File.Exists($"{packPath}~{i}"); i++)
                paths.Add($"{packPath}~{i}");

            _parts = new FileStream[paths.Count];
            _starts = new long[paths.Count];
            long offset = 0;
            for (var i = 0; i < paths.Count; i++)
            {
                _parts[i] = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
                _starts[i] = offset;
                offset += _parts[i].Length;
            }
            Total = offset;
        }

        /// <summary>Raw bytes at a global offset, spanning parts as needed.</summary>
        private void ReadRaw(long global, byte[] buffer, int count)
        {
            var pos = 0;
            while (pos < count)
            {
                var gi = global + pos;
                var k = _parts.Length - 1;
                while (k > 0 && _starts[k] > gi)
                    k--;
                var local = gi - _starts[k];
                _parts[k].Seek(local, SeekOrigin.Begin);
                var want = (int)Math.Min(count - pos, _parts[k].Length - local);
                var got = 0;
                while (got < want)
                {
                    var read = _parts[k].Read(buffer, pos + got, want - got);
                    if (read == 0)
                        break;
                    got += read;
                }
                pos += want;
            }
        }

        /// <summary>Decrypted bytes at a global offset: raw XOR the 129-byte key at that offset.</summary>
        public byte[] Decode(long global, int count)
        {
            var buffer = new byte[count];
            ReadRaw(global, buffer, count);
            var klen = _key.Length;
            for (var i = 0; i < count; i++)
                buffer[i] ^= _key[(int)((global + i) % klen)];
            return buffer;
        }

        public void Dispose()
        {
            foreach (var part in _parts)
                part.Dispose();
        }
    }
}

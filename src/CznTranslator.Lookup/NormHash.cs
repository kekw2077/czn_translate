using System.IO.Hashing;
using System.Text;

namespace CznTranslator.Lookup;

/// <summary>
/// xxHash64 over the UTF-8 bytes of a normalized string (TZ §5, column <c>norm_hash</c>).
/// SQLite has no unsigned 64-bit type, so the value is stored reinterpreted as a signed long;
/// <c>tools/czn/normalize.py</c> does the same reinterpretation so both sides index alike.
/// </summary>
public static class NormHash
{
    public static ulong Compute(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return XxHash64.HashToUInt64(bytes);
    }

    /// <summary>Value as stored in the <c>norm_hash</c> column.</summary>
    public static long ComputeSigned(string normalized) => unchecked((long)Compute(normalized));

    public static long ToSigned(ulong value) => unchecked((long)value);

    public static ulong ToUnsigned(long value) => unchecked((ulong)value);
}

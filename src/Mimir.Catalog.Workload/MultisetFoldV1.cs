using System.Buffers.Binary;

namespace Mimir.Catalog.Workload;

/// <summary>
/// MultisetFoldV1 — order-independent row fold over a relation.
///
/// Each logical row is canonical-encoded to bytes and hashed with an explicit
/// FNV-1a-64. Four commutative accumulators are maintained over the row hashes:
/// row count, unchecked UInt64 sum(hash), UInt64 xor(hash),
/// unchecked UInt64 sum(hash * hash). The final digest is the SHA-256 of the
/// canonical accumulator tuple, so the result is independent of physical scan
/// order while remaining multiplicity- and content-sensitive. Implementation is
/// explicit; no candidate-specific checksum may define correctness.
/// </summary>
public sealed class MultisetFoldV1
{
    public const int Version = 1;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private ulong _sum;
    private ulong _xor;
    private ulong _sumSq;
    private long _count;

    public long Count => _count;

    public static ulong HashRow(ReadOnlySpan<byte> row)
    {
        ulong h = FnvOffsetBasis;
        foreach (byte b in row)
        {
            h ^= b;
            h *= FnvPrime;
        }
        return h;
    }

    public void Add(byte[] canonicalRow)
    {
        ulong h = HashRow(canonicalRow);
        unchecked
        {
            _sum += h;
            _xor ^= h;
            _sumSq += h * h;
        }
        _count++;
    }

    public string Digest()
    {
        var b = new Canon.Builder();
        b.AddLong(_count).AddUInt64(_sum).AddUInt64(_xor).AddUInt64(_sumSq);
        return b.ToSha256Hex();
    }

    // ---- canonical row encodings (schema-frozen; do not depend on JSON order) ----

    public static byte[] ConceptRow(long qid, bool inT1, bool inT2)
    {
        var b = new Canon.Builder();
        b.AddLong(qid).AddByte(inT1 ? (byte)1 : (byte)0).AddByte(inT2 ? (byte)1 : (byte)0);
        return b.ToArray();
    }

    public static byte[] LexicalRow(long qid, string lang, string kind, string value)
    {
        var b = new Canon.Builder();
        b.AddLong(qid).AddString(lang).AddString(kind).AddString(value);
        return b.ToArray();
    }

    public static byte[] EdgeRow(long subject, long target)
    {
        var b = new Canon.Builder();
        b.AddLong(subject).AddLong(target);
        return b.ToArray();
    }

    /// <summary>Big-endian int64 image helper for scalar digests.</summary>
    public static byte[] LongRow(long value)
    {
        var b = new Canon.Builder();
        b.AddLong(value);
        return b.ToArray();
    }
}

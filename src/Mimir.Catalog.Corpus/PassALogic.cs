namespace Mimir.Catalog.Corpus;

/// <summary>
/// Shared pure Pass-A logic kept testable without a full source scan.
/// </summary>
public static class PassALogic
{
    public const int OverflowDegree = 10_000;

    /// <summary>presence flags: bit 0 = en label present, bit 1 = nb label present.</summary>
    public static int PresenceFlags(bool labelEnPresent, bool labelNbPresent) =>
        (labelEnPresent ? 1 : 0) | (labelNbPresent ? 2 : 0);

    /// <summary>
    /// T2 structural discovery: the structural tier is exactly the union of the
    /// distinct P279 endpoints (subject and each object). No recursive closure.
    /// </summary>
    public static void AddP279Endpoints(HashSet<long> endpoints, long subjectQid, IEnumerable<long> objectTargets)
    {
        endpoints.Add(subjectQid);
        foreach (long t in objectTargets)
            endpoints.Add(t);
    }

    public static void BumpDegree(Dictionary<long, long> hist, int degree)
    {
        if (degree >= OverflowDegree)
            degree = OverflowDegree; // overflow bucket
        hist.TryGetValue(degree, out long c);
        hist[degree] = c + 1;
    }

    public sealed record DegreeStats(long ItemCount, long Min, long Max, double Median, double P90, double P95, double P99);

    /// <summary>
    /// Quantiles over the outgoing-degree histogram (item-weighted, exact over
    /// the stored buckets; overflow values are folded into the overflow bucket).
    /// </summary>
    public static DegreeStats DegreeSummary(Dictionary<long, long> hist)
    {
        if (hist.Count == 0)
            return new DegreeStats(0, 0, 0, 0, 0, 0, 0);
        long total = hist.Values.Sum();
        long min = hist.Keys.Min(), max = hist.Keys.Max();

        double Quantile(double q)
        {
            long target = (long)Math.Ceiling(q * total);
            long acc = 0;
            foreach (var kv in hist.OrderBy(k => k.Key))
            {
                acc += kv.Value;
                if (acc >= target) return kv.Key;
            }
            return max;
        }

        return new DegreeStats(total, min, max, Quantile(0.50), Quantile(0.90), Quantile(0.95), Quantile(0.99));
    }

    public static (long T2Only, long T1UnionT2) TierArithmetic(long t1, long t2, long t1IntersectT2)
    {
        long t2Only = t2 - t1IntersectT2;
        return (t2Only, t1 + t2Only);
    }
}

/// <summary>
/// Compact deterministic T2 persistence: ascending, deduplicated, little-endian
/// Int64 records. This is a corpus-builder intermediate artifact, not a
/// production catalog format.
/// </summary>
public static class T2Persistence
{
    public const int RecordBytes = 8;

    public static long WriteEndpoints(string path, IReadOnlyList<long> sortedUniqueEndpoints)
    {
        using var fs = File.Create(path);
        Span<byte> buf = stackalloc byte[8];
        foreach (long q in sortedUniqueEndpoints)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf, q);
            fs.Write(buf);
        }
        return fs.Length;
    }

    public static long[] ReadEndpoints(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        if (all.Length % RecordBytes != 0)
            throw new InvalidDataException($"endpoint artifact size {all.Length} is not a multiple of {RecordBytes}");
        var result = new long[all.Length / RecordBytes];
        for (int i = 0; i < result.Length; i++)
            result[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(all.AsSpan(i * RecordBytes, RecordBytes));
        return result;
    }
}

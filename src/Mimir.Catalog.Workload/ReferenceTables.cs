using Parquet;
using Parquet.Schema;

namespace Mimir.Catalog.Workload;

/// <summary>
/// In-memory reference tables loaded from the accepted canonical Pass-B corpus.
/// These are the neutral oracle inputs; never a storage-candidate representation.
/// </summary>
public sealed class ConceptTable
{
    private readonly long[] _qids;
    private readonly byte[] _flags; // bit0 = InT1, bit1 = InT2
    public long Total { get; }
    public long T1OnlyTotal { get; }
    public long CapTotal { get; }
    public long T2OnlyTotal { get; } // includes unobserved tail rows
    public long TailCount { get; }
    public IReadOnlyList<long> TailQids { get; }

    public ConceptTable(long[] qids, byte[] flags, long total, long t1Only, long cap, long t2Only, long tailCount, IReadOnlyList<long> tailQids)
    {
        _qids = qids;
        _flags = flags;
        Total = total;
        T1OnlyTotal = t1Only;
        CapTotal = cap;
        T2OnlyTotal = t2Only;
        TailCount = tailCount;
        TailQids = tailQids;
    }

    public int Count => _qids.Length;

    public long[] Qids => _qids;
    public byte[] Flags => _flags;

    public bool TryGet(long qid, out bool inT1, out bool inT2)
    {
        int lo = 0, hi = _qids.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_qids[mid] < qid) lo = mid + 1;
            else if (_qids[mid] > qid) hi = mid - 1;
            else
            {
                inT1 = (_flags[mid] & 1) != 0;
                inT2 = (_flags[mid] & 2) != 0;
                return true;
            }
        }
        inT1 = false;
        inT2 = false;
        return false;
    }

    /// <summary>Measured T2-only pool excludes the unobserved tail rows.</summary>
    public long T2OnlyObservedCount => T2OnlyTotal - TailCount;
}

/// <summary>Adjacency (subject -> sorted targets) plus target fanout counts.</summary>
public sealed class EdgeTable
{
    public string Relation { get; }
    private readonly Dictionary<long, long[]> _adj;
    public Dictionary<long, long> TargetCounts { get; }
    public long RowCount { get; }

    public EdgeTable(string relation, Dictionary<long, long[]> adj, Dictionary<long, long> targetCounts, long rowCount)
    {
        Relation = relation;
        _adj = adj;
        TargetCounts = targetCounts;
        RowCount = rowCount;
    }

    public IReadOnlyCollection<long> Subjects => _adj.Keys;
    public int DistinctSubjects => _adj.Count;

    public int? DegreeOf(long subject) => _adj.TryGetValue(subject, out var t) ? t.Length : null;

    public bool TryGetTargets(long subject, out long[] targets) => _adj.TryGetValue(subject, out targets);
}

/// <summary>Lexical domain statistics from the canonical lexical relation.</summary>
public sealed class LexicalStats
{
    public long RowCount { get; }
    public HashSet<long> WithLexical { get; }
    public Dictionary<(string Lang, string Value), long> Fanout { get; }
    public long DistinctKeys { get; }
    public long Bin1 { get; }
    public long Bin2To5 { get; }
    public long Bin6To50 { get; }
    public long Bin51Plus { get; }

    public LexicalStats(long rowCount, HashSet<long> withLexical, Dictionary<(string, string), long> fanout)
    {
        RowCount = rowCount;
        WithLexical = withLexical;
        Fanout = fanout;
        DistinctKeys = fanout.Count;
        long b1 = 0, b2 = 0, b6 = 0, b51 = 0;
        foreach (var kv in fanout)
        {
            if (kv.Value == 1) b1++;
            else if (kv.Value <= 5) b2++;
            else if (kv.Value <= 50) b6++;
            else b51++;
        }
        Bin1 = b1;
        Bin2To5 = b2;
        Bin6To50 = b6;
        Bin51Plus = b51;
    }

    public int FanoutBin((string Lang, string Value) key)
    {
        long c = Fanout.TryGetValue(key, out var v) ? v : 0;
        if (c == 0) return 0;
        if (c == 1) return 1;
        if (c <= 5) return 2;
        if (c <= 50) return 3;
        return 4;
    }
}

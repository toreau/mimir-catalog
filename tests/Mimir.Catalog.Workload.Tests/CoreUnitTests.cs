using System.Text;
using System.Text.Json;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Workload.Tests;

public class CoreUnitTests
{
    [Fact]
    public void CanonicalIdentity_Unambiguous_And_CaseSensitive()
    {
        Assert.NotEqual(Canon.LexicalIdentity("en", "ab"), Canon.LexicalIdentity("ena", "b"));
        Assert.NotEqual(Canon.LexicalIdentity("en", "Abc"), Canon.LexicalIdentity("en", "abc"));
        Assert.NotEqual(Canon.LexicalIdentity("en", "a\u001fb"), Canon.LexicalIdentity("en", "a b"));
    }

    [Fact]
    public void Canon_Utf8_Stable()
    {
        Assert.Equal(Canon.Sha256Hex("abc"), Canon.Sha256Hex("abc"));
        Assert.NotEqual(Canon.Sha256Hex("abc"), Canon.Sha256Hex("abd"));
        Assert.Equal(64, Canon.Sha256Hex("abc").Length);
    }

    [Fact]
    public void MultisetFold_IndependentOfRowOrder()
    {
        byte[] a = MultisetFoldV1.ConceptRow(5, true, false);
        byte[] b = MultisetFoldV1.ConceptRow(9, false, true);
        byte[] c = MultisetFoldV1.ConceptRow(12, true, true);
        string f1 = Fold(a, b, c);
        string f2 = Fold(c, a, b);
        Assert.Equal(f1, f2);
    }

    [Fact]
    public void MultisetFold_SensitiveToContentAndMultiplicity()
    {
        byte[] a = MultisetFoldV1.ConceptRow(5, true, false);
        byte[] b = MultisetFoldV1.ConceptRow(6, true, false);
        Assert.NotEqual(Fold(a), Fold(b));
        Assert.NotEqual(Fold(a), Fold(a, a));
    }

    private static string Fold(params byte[][] rows)
    {
        var f = new MultisetFoldV1();
        foreach (var r in rows) f.Add(r);
        return f.Digest();
    }

    [Fact]
    public void LexicalStats_BinClassification_Exact()
    {
        var fan = new Dictionary<(string, string), long>
        {
            [("en", "a")] = 1,
            [("en", "b")] = 1,
            [("nb", "c")] = 2,
            [("nb", "d")] = 7,
            [("en", "e")] = 99,
        };
        var stats = new LexicalStats(5, new HashSet<long>(), fan);
        Assert.Equal(2, stats.Bin1);
        Assert.Equal(1, stats.Bin2To5);
        Assert.Equal(1, stats.Bin6To50);
        Assert.Equal(1, stats.Bin51Plus);
    }

    [Fact]
    public void ConceptTable_TryGet_Tiers()
    {
        var ct = new ConceptTable(new[] { 7L, 9L }, new byte[] { 1, 2 }, 2, 1, 0, 1, 0, Array.Empty<long>());
        Assert.True(ct.TryGet(7, out bool i1, out bool i2));
        Assert.True(i1 && !i2);
        Assert.False(ct.TryGet(8, out _, out _));
    }

    [Fact]
    public void RankTopQids_Deterministic_OrderIndependent()
    {
        var pool = Enumerable.Range(1, 500).Select(i => (long)i * 7).ToList();
        var a = WorkloadSelection.RankTopQids(pool, "d", "S1", "T1Only", 25).Select(x => x.Qid).ToArray();
        var shuffled = pool.OrderByDescending(x => x).ToList();
        var b = WorkloadSelection.RankTopQids(shuffled, "d", "S1", "T1Only", 25).Select(x => x.Qid).ToArray();
        Assert.Equal(a, b);
        Assert.Equal(25, a.Length);
        Assert.Equal(25, a.Distinct().Count());
    }

    [Fact]
    public void ConceptMisses_ProvenAbsent_NoDuplicates()
    {
        var present = new HashSet<long> { 1, 2, 3, 100 };
        var misses = WorkloadSelection.GenerateConceptMisses(present.Contains, "miss-domain", "S1", "Absent", 400);
        Assert.Equal(400, misses.Length);
        Assert.Equal(400, misses.Select(m => m.Qid).Distinct().Count());
        foreach (var m in misses) Assert.False(present.Contains(m.Qid));
    }

    [Fact]
    public void LexicalMisses_ProvenAbsent_NoDuplicates()
    {
        var keys = new HashSet<(string, string)> { ("nb", "mc-miss-x") };
        var misses = WorkloadSelection.GenerateLexicalMisses(
            (l, v) => keys.Contains((l, v)), "miss-lex", "S2", "Miss", 300, new[] { "nb" });
        Assert.Equal(300, misses.Length);
        Assert.Equal(300, misses.Select(m => m.Lang + m.Value).Distinct().Count());
        foreach (var m in misses) Assert.False(keys.Contains((m.Lang, m.Value)));
    }

    [Fact]
    public void RoundRobinInterleave_OrderAndCompleteness()
    {
        var a = new[] { "A0", "A1", "A2", "A3" };
        var b = new[] { "B0", "B1" };
        var c = new[] { "C0" };
        var merged = WorkloadSelection.RoundRobinInterleave<string>(new[] { a, b, c });
        Assert.Equal(new[] { "A0", "B0", "C0", "A1", "B1", "A2", "A3" }, merged.ToArray());
        Assert.Equal(7, merged.Count);
        Assert.Equal(merged, WorkloadSelection.RoundRobinInterleave<string>(new[] { a, b, c }));
    }

    [Fact]
    public void GraphTraversal_BfsSemantics()
    {
        var parents = new Dictionary<long, long[]>
        {
            [1] = new[] { 2L, 3L },
            [2] = new[] { 4L },
            [3] = new[] { 2L },
            [4] = new[] { 1L },
        };
        var r3 = GraphTraversal.Ancestry(1, 3, 5000, s => parents.TryGetValue(s, out var p) ? p : Array.Empty<long>());
        Assert.False(r3.ExceededGuard);
        Assert.Equal(new long[] { 2, 3, 4 }, r3.Discovered);
        Assert.DoesNotContain(1L, r3.Discovered);
        Assert.Equal(4, r3.VisitedCount);

        var r1 = GraphTraversal.Ancestry(1, 1, 5000, s => parents.TryGetValue(s, out var p) ? p : Array.Empty<long>());
        Assert.Equal(new long[] { 2, 3 }, r1.Discovered);
    }

    [Fact]
    public void GraphTraversal_GuardRejectsOversized()
    {
        var r = GraphTraversal.Ancestry(1, 3, 10, s => s == 1 ? Enumerable.Range(2, 20).Select(i => (long)i).ToArray() : Array.Empty<long>());
        Assert.True(r.ExceededGuard);
        Assert.Equal(10, r.VisitedCount);
    }

    [Fact]
    public void WorkloadMetrics_PercentilesAndMedianOfSummaries()
    {
        var sorted = new double[] { 1, 2, 3, 4, 100 };
        Assert.Equal(3, WorkloadMetrics.Percentile(sorted, 0.5));
        Assert.Equal(4, WorkloadMetrics.Percentile(sorted, 0.75));
        Assert.Equal(100, WorkloadMetrics.Percentile(sorted, 1.0));
        Assert.Equal(10, WorkloadMetrics.MedianOfSummaries(new[] { 5.0, 10.0, 20.0 }));
    }

    [Fact]
    public void WorkloadIdentity_Stable_And_ParameterSensitive()
    {
        var c1 = new WorkloadContractV1();
        var c2 = new WorkloadContractV1();
        var artifacts = new[] { ("concept.parquet", "aa".PadRight(64, 'a')) };
        string id1 = WorkloadIdentity.Compute(c1, "c", artifacts, "f");
        string id2 = WorkloadIdentity.Compute(c2, "c", artifacts, "f");
        Assert.Equal(64, id1.Length);
        Assert.Equal(id1, id2);
        var c3 = new WorkloadContractV1 { MaxDepth = 4 };
        Assert.NotEqual(id1, WorkloadIdentity.Compute(c3, "c", artifacts, "f"));
        Assert.NotEqual(id1, WorkloadIdentity.Compute(c1, "other", artifacts, "f"));
    }

    [Fact]
    public void WorkloadIdentity_ExcludesOperationalData()
    {
        var c = new WorkloadContractV1();
        var artifacts = new[] { ("concept.parquet", "bb".PadRight(64, 'b')) };
        string id = WorkloadIdentity.Compute(c, "c", artifacts, "f");
        Assert.DoesNotContain(DateTime.UtcNow.Year.ToString(), id);
        Assert.DoesNotContain("data/benchmarks", id);
    }
}

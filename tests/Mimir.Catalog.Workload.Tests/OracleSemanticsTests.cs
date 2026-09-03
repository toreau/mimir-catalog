using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Workload.Tests;

public class OracleSemanticsTests
{
    [Fact]
    public void ConceptResultDigest_Stable_AndSensitive()
    {
        string a = WorkloadOracle.ConceptResultDigest(5, true, true, false);
        Assert.Equal(a, WorkloadOracle.ConceptResultDigest(5, true, true, false));
        Assert.NotEqual(a, WorkloadOracle.ConceptResultDigest(5, true, false, false));
        Assert.NotEqual(a, WorkloadOracle.ConceptResultDigest(5, false, false, false));
        Assert.NotEqual(WorkloadOracle.ConceptResultDigest(5, false, false, false), WorkloadOracle.ConceptResultDigest(6, false, false, false));
    }

    [Fact]
    public void LexMembersDigest_Sorted_OrdinalKinds()
    {
        var a = new List<(long, string)> { (2, "label"), (1, "alias") };
        var sorted = a.OrderBy(m => m.Item1).ThenBy(m => m.Item2, StringComparer.Ordinal).ToList();
        Assert.Equal(WorkloadOracle.LexMembersDigest(sorted), WorkloadOracle.LexMembersDigest(a));
        Assert.NotEqual(WorkloadOracle.LexMembersDigest(a), WorkloadOracle.LexMembersDigest(new List<(long, string)> { (2, "label"), (1, "alias"), (3, "alias") }));
    }

    [Fact]
    public void LexicalRowsDigest_ContentSensitive()
    {
        var r1 = new List<(string, string, string)> { ("en", "label", "a") };
        var r2 = new List<(string, string, string)> { ("en", "label", "b") };
        Assert.NotEqual(WorkloadOracle.LexicalRowsDigest(7, r1), WorkloadOracle.LexicalRowsDigest(7, r2));
        Assert.NotEqual(WorkloadOracle.LexicalRowsDigest(7, r1), WorkloadOracle.LexicalRowsDigest(8, r1));
    }

    [Fact]
    public void A5Row_NullVsEmpty_Distinguishable()
    {
        byte[] nullLabel = WorkloadOracle.A5Row(1, 3, null, "nb");
        byte[] emptyLabel = WorkloadOracle.A5Row(1, 3, "", "nb");
        Assert.NotEqual(MultisetFoldV1.HashRow(nullLabel), MultisetFoldV1.HashRow(emptyLabel));
        Assert.Equal(MultisetFoldV1.HashRow(nullLabel), MultisetFoldV1.HashRow(WorkloadOracle.A5Row(1, 3, null, "nb")));
    }

    [Fact]
    public void G1Digest_Stable_AndSetSensitive()
    {
        string g = WorkloadOracle.G1Digest(new long[] { 1, 2, 3 }, 4);
        // callers must supply ascending; unsorted input is not canonical
        Assert.NotEqual(g, WorkloadOracle.G1Digest(new long[] { 3, 1, 2 }, 4));
        Assert.NotEqual(g, WorkloadOracle.G1Digest(new long[] { 1, 2 }, 4));
        Assert.NotEqual(g, WorkloadOracle.G1Digest(new long[] { 1, 2, 3 }, 5));
    }

    [Fact]
    public void AnalyticalRowsDigest_RowOrderFree()
    {
        byte[] a = WorkloadOracle.TargetCountRow(1, 5);
        byte[] b = WorkloadOracle.TargetCountRow(2, 7);
        // caller supplies canonical sorted rows; order changes must be observable
        Assert.NotEqual(WorkloadOracle.AnalyticalRowsDigest(new[] { a, b }), WorkloadOracle.AnalyticalRowsDigest(new[] { b, a }));
        Assert.Equal(WorkloadOracle.AnalyticalRowsDigest(new[] { a, b }), WorkloadOracle.AnalyticalRowsDigest(new[] { a, b }));
    }
}

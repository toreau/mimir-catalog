using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class A2A4RunnerTests
{
    private sealed class Fake : IAnalyticalCandidate
    {
        public List<(string Lang, string LexKind, long Count)> A2 { get; set; } = new();
        public List<(long TargetQid, long Count)> A3 { get; set; } = new();
        public List<(long TargetQid, long Count)> A4 { get; set; } = new();
        public bool ThrowA2 { get; set; }
        public int OpenCalls { get; private set; }

        public void Open() => OpenCalls++;
        public void Dispose() { }
        public IEnumerable<ConceptRow> ScanConcept() => Array.Empty<ConceptRow>();
        public IEnumerable<LexicalRow> ScanLexicalEntry() => Array.Empty<LexicalRow>();
        public IEnumerable<EdgeRow> ScanInstanceOf() => Array.Empty<EdgeRow>();
        public IEnumerable<EdgeRow> ScanSubclassOf() => Array.Empty<EdgeRow>();
        public IReadOnlyList<(string Lang, string LexKind, long Count)> A2LangKindCounts()
        {
            if (ThrowA2) throw new InvalidOperationException("boom");
            return A2;
        }
        public IReadOnlyList<(long TargetQid, long Count)> A3P31Fanout() => A3;
        public IReadOnlyList<(long TargetQid, long Count)> A4P279Fanout() => A4;
        public IReadOnlyList<A5Row> A5P31TargetLabels() => throw new NotSupportedException();
    }

    private static AnalyticalWorkload ExpectedFor(Fake a)
    {
        var exp = new Dictionary<string, A1Expected>(StringComparer.Ordinal);
        var a2rows = a.A2.OrderBy(r => r.Lang, StringComparer.Ordinal).ThenBy(r => r.LexKind, StringComparer.Ordinal)
            .Select(r => WorkloadOracle.LangKindCountRow(r.Lang, r.LexKind, r.Count)).ToArray();
        exp["A2"] = new("A2", a.A2.Count, WorkloadOracle.AnalyticalRowsDigest(a2rows));
        var a3rows = a.A3.OrderBy(r => r.TargetQid).Select(r => WorkloadOracle.TargetCountRow(r.TargetQid, r.Count)).ToArray();
        exp["A3"] = new("A3", a.A3.Count, WorkloadOracle.AnalyticalRowsDigest(a3rows));
        var a4rows = a.A4.OrderBy(r => r.TargetQid).Select(r => WorkloadOracle.TargetCountRow(r.TargetQid, r.Count)).ToArray();
        exp["A4"] = new("A4", a.A4.Count, WorkloadOracle.AnalyticalRowsDigest(a4rows));
        return new AnalyticalWorkload { Expected = exp };
    }

    private static Fake Sample()
    {
        return new Fake
        {
            A2 = new List<(string, string, long)> { ("label", "en", 2), ("en", "label", 3) }, // deliberately unsorted + duplicate tuple counts as two output groups
            A3 = new List<(long, long)> { (10, 2), (5, 1) },
            A4 = new List<(long, long)> { (50, 3), (10, 4) },
        };
    }

    [Fact]
    public void CorrectOps_Valid_AndNoOpen()
    {
        var a = Sample();
        var results = new A2A4CorrectnessRunner(a).RunAll(ExpectedFor(a));
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(ServingStatuses.Valid, r.Status));
        Assert.Equal(0, a.OpenCalls);
    }

    [Fact]
    public void A2_OrdinalCanonicalization_AndWrongAggregate_Invalid()
    {
        var a = Sample(); // deliberately unsorted rows canonicalized ordinally
        Assert.Equal(ServingStatuses.Valid, new A2A4CorrectnessRunner(a).RunAll(ExpectedFor(a))[0].Status);

        // Wrong aggregate count -> INVALID.
        var mutated = new Fake { A2 = new List<(string, string, long)> { ("en", "label", 99) } };
        var emptyDigest = WorkloadOracle.AnalyticalRowsDigest(Array.Empty<byte[]>());
        var expectedA2 = new AnalyticalWorkload
        {
            Expected = new Dictionary<string, A1Expected>(StringComparer.Ordinal)
            {
                ["A2"] = new("A2", 1, WorkloadOracle.AnalyticalRowsDigest(
                    new[] { WorkloadOracle.LangKindCountRow("en", "label", 2) })),
                ["A3"] = new("A3", 0, emptyDigest),
                ["A4"] = new("A4", 0, emptyDigest),
            },
        };
        var r = new A2A4CorrectnessRunner(mutated).RunAll(expectedA2);
        Assert.Equal(ServingStatuses.Invalid, r[0].Status); // count/digest mismatch
    }

    [Fact]
    public void A3A4_Unsorted_Valid_AndWrongValue_Invalid()
    {
        var a = new Fake
        {
            A3 = new List<(long, long)> { (20, 1), (5, 2) },
            A4 = new List<(long, long)> { (5, 9) },
        };
        var results = new A2A4CorrectnessRunner(a).RunAll(ExpectedFor(a));
        Assert.Equal(ServingStatuses.Valid, results[1].Status);
        Assert.Equal(ServingStatuses.Valid, results[2].Status);

        var wrong = new Fake { A4 = new List<(long, long)> { (5, 8) } };
        var wrongWorkload = new AnalyticalWorkload
        {
            Expected = new Dictionary<string, A1Expected>(StringComparer.Ordinal)
            {
                ["A2"] = new("A2", 0, WorkloadOracle.AnalyticalRowsDigest(Array.Empty<byte[]>())),
                ["A3"] = new("A3", 0, WorkloadOracle.AnalyticalRowsDigest(Array.Empty<byte[]>())),
                ["A4"] = new("A4", 1, WorkloadOracle.AnalyticalRowsDigest(
                    new[] { WorkloadOracle.TargetCountRow(5, 7) })),
            },
        };
        Assert.Equal(ServingStatuses.Invalid, new A2A4CorrectnessRunner(wrong).RunAll(wrongWorkload)[2].Status);
    }

    [Fact]
    public void Exception_Error_AndContinuation()
    {
        var a = Sample();
        a.ThrowA2 = true;
        var results = new A2A4CorrectnessRunner(a).RunAll(ExpectedFor(a));
        Assert.Equal(ServingStatuses.Error, results[0].Status);
        Assert.Equal(ServingStatuses.Valid, results[1].Status);
        Assert.Equal(ServingStatuses.Valid, results[2].Status);
    }
}

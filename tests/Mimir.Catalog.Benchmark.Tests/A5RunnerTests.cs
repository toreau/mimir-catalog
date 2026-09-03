using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class A5RunnerTests
{
    private sealed class Fake : IAnalyticalCandidate
    {
        public IReadOnlyList<A5Row> Rows { get; set; } = Array.Empty<A5Row>();
        public bool Throw { get; set; }
        public int OpenCalls { get; private set; }
        public void Open() => OpenCalls++;
        public void Dispose() { }
        public IEnumerable<ConceptRow> ScanConcept() => Array.Empty<ConceptRow>();
        public IEnumerable<LexicalRow> ScanLexicalEntry() => Array.Empty<LexicalRow>();
        public IEnumerable<EdgeRow> ScanInstanceOf() => Array.Empty<EdgeRow>();
        public IEnumerable<EdgeRow> ScanSubclassOf() => Array.Empty<EdgeRow>();
        public IReadOnlyList<(string Lang, string LexKind, long Count)> A2LangKindCounts() => throw new NotSupportedException();
        public IReadOnlyList<(long TargetQid, long Count)> A3P31Fanout() => throw new NotSupportedException();
        public IReadOnlyList<(long TargetQid, long Count)> A4P279Fanout() => throw new NotSupportedException();
        public IReadOnlyList<A5Row> A5P31TargetLabels()
        {
            if (Throw) throw new InvalidOperationException("boom");
            return Rows;
        }
    }

    private static AnalyticalWorkload WorkloadFor(IReadOnlyList<A5Row> rows)
    {
        var sorted = rows.OrderBy(r => r.TargetQid)
            .Select(r => WorkloadOracle.A5Row(r.TargetQid, r.Fanout, r.EnLabel, r.NbLabel)).ToArray();
        var exp = new Dictionary<string, A1Expected>(StringComparer.Ordinal)
        {
            ["A5"] = new("A5", rows.Count, WorkloadOracle.AnalyticalRowsDigest(sorted)),
        };
        return new AnalyticalWorkload { Expected = exp };
    }

    [Fact]
    public void CorrectA5_Unsorted_Valid_NoOpen()
    {
        var rows = new List<A5Row>
        {
            new(20, 2, "nb", null),
            new(5, 1, null, null),
            new(7, 1, "E", "N"),
        };
        var a = new Fake { Rows = rows };
        var r = new A5CorrectnessRunner(a).Run(WorkloadFor(rows));
        Assert.Equal(ServingStatuses.Valid, r.Status);
        Assert.Equal(0, a.OpenCalls);
    }

    [Fact]
    public void WrongFanoutLabelAndNullability_Invalid()
    {
        var a = new Fake { Rows = new List<A5Row> { new(5, 1, "E", null) } };
        // expected built from a different (wrong) semantic row
        var wrong = new List<A5Row> { new(5, 2, "X", "Y") };
        Assert.Equal(ServingStatuses.Invalid, new A5CorrectnessRunner(a).Run(WorkloadFor(wrong)).Status);

        // empty string distinct from null: adapter empty vs expected null
        var emptyAdapter = new Fake { Rows = new List<A5Row> { new(5, 1, "", null) } };
        var nullExpected = new List<A5Row> { new(5, 1, null, null) };
        Assert.Equal(ServingStatuses.Invalid, new A5CorrectnessRunner(emptyAdapter).Run(WorkloadFor(nullExpected)).Status);
    }

    [Fact]
    public void Exception_Error()
    {
        var a = new Fake { Rows = new List<A5Row> { new(1, 1, null, null) }, Throw = true };
        var r = new A5CorrectnessRunner(a).Run(WorkloadFor(a.Rows));
        Assert.Equal(ServingStatuses.Error, r.Status);
        Assert.NotNull(r.ErrorMessage);
    }
}

using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class G1RunnerTests
{
    private sealed class FakeAdapter : IStorageCandidate
    {
        public Dictionary<long, long[]> Parents { get; set; } = new();
        public Func<long, long[]>? Throwing { get; set; }
        public int OpenCalls { get; private set; }
        public void Open() => OpenCalls++;
        public void Dispose() { }
        public ConceptHit GetConcept(long q) => new(false, false, false);
        public IReadOnlyList<LexicalHit> LookupLexical(string l, string v) => Array.Empty<LexicalHit>();
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long q) => Array.Empty<LexicalRow>();
        public IReadOnlyList<long> GetInstanceOf(long q) => Array.Empty<long>();
        public IReadOnlyList<long> GetSubclassOf(long q)
        {
            if (Throwing != null) return Throwing(q);
            return Parents.TryGetValue(q, out var p) ? p : Array.Empty<long>();
        }
    }

    private static GraphProbe P(long seq, long start, string stratum = "Degree1") => new("G1", seq, stratum, true, start);

    private static GraphExpected ExpectedFor(FakeAdapter a, GraphProbe probe)
    {
        var t = GraphTraversal.Ancestry(probe.StartQid, 3, 5000, q =>
            (a.Parents.TryGetValue(q, out var p) ? p : Array.Empty<long>()).OrderBy(x => x).ToArray());
        return new GraphExpected("G1", probe.Seq, true, t.Discovered.Length, t.VisitedCount,
            WorkloadOracle.G1Digest(t.Discovered, t.VisitedCount));
    }

    private static GraphWorkload W(IEnumerable<(GraphProbe Probe, GraphExpected Expected)> items)
    {
        var list = items.ToList();
        return new GraphWorkload
        {
            Probes = list.Select(i => i.Probe).ToList(),
            Expected = list.ToDictionary(i => ("G1", i.Probe.Seq), i => i.Expected),
        };
    }

    [Fact]
    public void Traversal_DepthSemantics_AndCorrectness_Valid()
    {
        var a = new FakeAdapter
        {
            Parents = new Dictionary<long, long[]>
            {
                [1] = new[] { 2L, 3L },   // depth1 (unsorted provided; canonicalized)
                [2] = new[] { 4L },       // depth2
                [3] = new[] { 2L, 5L },   // duplicate parent 2 (dedup), 5 depth2
                [4] = new[] { 6L },       // depth3 included
                [6] = new[] { 7L },       // depth4 node must not be expanded
            },
        };
        var probe = P(0, 1);
        var expected = ExpectedFor(a, probe);
        var r = new G1CorrectnessRunner(a).RunAll(W([(probe, expected)]))[0];
        Assert.Equal(ServingStatuses.Valid, r.Status);
        Assert.Equal(expected.Cardinality, r.ActualCardinality);
        Assert.Equal(expected.Visited, r.ActualVisited);
    }

    [Fact]
    public void Traversal_StartNotDiscovered_CycleAndDiamond()
    {
        var a = new FakeAdapter
        {
            Parents = new Dictionary<long, long[]>
            {
                [1] = new[] { 2L },
                [2] = new[] { 1L, 3L }, // cycle back to start + 3
                [3] = new[] { 2L },     // duplicate 2 already visited (diamond)
            },
        };
        var probe = P(0, 1);
        var expected = ExpectedFor(a, probe);
        var r = new G1CorrectnessRunner(a).RunAll(W([(probe, expected)]))[0];
        Assert.Equal(ServingStatuses.Valid, r.Status);
        Assert.Equal(2L, r.ActualCardinality); // discovered {2,3}; start not auto-emitted, diamond 2 deduplicated
        var traversal = new G1CorrectnessRunner(a).Traverse(probe);
        Assert.DoesNotContain(1L, traversal.Discovered);
    }

    [Fact]
    public void Traversal_Mismatch_Invalid()
    {
        var a = new FakeAdapter { Parents = new Dictionary<long, long[]> { [1] = new[] { 2L } } };
        var probe = P(0, 1);
        var expected = ExpectedFor(a, probe);
        var wrongCard = expected with { Cardinality = expected.Cardinality + 1 };
        var wrongVisited = expected with { Visited = expected.Visited + 1 };
        var wrongDigest = expected with { Digest = "0".PadRight(64, '0') };
        var runner = new G1CorrectnessRunner(a);
        Assert.Equal(ServingStatuses.Invalid, runner.RunAll(W([(probe, wrongCard)]))[0].Status);
        Assert.Equal(ServingStatuses.Invalid, runner.RunAll(W([(probe, wrongVisited)]))[0].Status);
        Assert.Equal(ServingStatuses.Invalid, runner.RunAll(W([(probe, wrongDigest)]))[0].Status);
    }

    [Fact]
    public void GuardExceed_Error_Visited5000()
    {
        var a = new FakeAdapter
        {
            Parents = new Dictionary<long, long[]> { [1] = Enumerable.Range(2, 6000).Select(i => (long)i).ToArray() },
        };
        var probe = P(0, 1);
        var expected = new GraphExpected("G1", 0, true, 4999, 5000, "e");
        var r = new G1CorrectnessRunner(a).RunAll(W([(probe, expected)]))[0];
        Assert.Equal(ServingStatuses.Error, r.Status);
        Assert.Equal(5000L, r.ActualVisited);
        Assert.Contains("guard", r.ErrorMessage ?? "");
    }

    [Fact]
    public void AdapterException_Error_AndNextProbeStillRuns()
    {
        var a = new FakeAdapter
        {
            Parents = new Dictionary<long, long[]> { [1] = new[] { 2L }, [2] = Array.Empty<long>() },
        };
        a.Throwing = q => q == 1 ? throw new InvalidOperationException("boom") : a.Parents[q];
        var good = P(1, 2);
        var bad = P(0, 1);
        var eBad = ExpectedFor(a, bad with { StartQid = 1 });
        var eGood = ExpectedFor(a, good);
        var results = new G1CorrectnessRunner(a).RunAll(W([(bad, eBad), (good, eGood)]));
        Assert.Equal(ServingStatuses.Error, results[0].Status);
        Assert.Equal(ServingStatuses.Valid, results[1].Status);
    }

    [Fact]
    public void Runner_NeverOpens_AndStartsAreIndependent()
    {
        var a = new FakeAdapter
        {
            Parents = new Dictionary<long, long[]>
            {
                [1] = new[] { 2L },
                [2] = Array.Empty<long>(),
                [100] = new[] { 200L },
                [200] = new[] { 300L },
            },
        };
        var p1 = P(0, 1);
        var p2 = P(1, 100);
        var results = new G1CorrectnessRunner(a).RunAll(W([(p1, ExpectedFor(a, p1)), (p2, ExpectedFor(a, p2))]));
        Assert.Equal(0, a.OpenCalls);
        Assert.All(results, r => Assert.Equal(ServingStatuses.Valid, r.Status));
    }
}

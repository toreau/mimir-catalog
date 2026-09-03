using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class G2RunnerTests
{
    private sealed class FakeAdapter : IStorageCandidate
    {
        public Dictionary<long, long[]> Instance { get; set; } = new();
        public Dictionary<long, long[]> Parents { get; set; } = new();
        public List<long> InstanceCalls { get; } = new();
        public int SubclassCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public long? ThrowInstanceFor { get; set; }
        public long? ThrowSubclassFor { get; set; }

        public void Open() => OpenCalls++;
        public void Dispose() { }
        public ConceptHit GetConcept(long q) => new(false, false, false);
        public IReadOnlyList<LexicalHit> LookupLexical(string l, string v) => Array.Empty<LexicalHit>();
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long q) => Array.Empty<LexicalRow>();
        public IReadOnlyList<long> GetInstanceOf(long q)
        {
            InstanceCalls.Add(q);
            if (ThrowInstanceFor == q) throw new InvalidOperationException("boom-instance");
            return Instance.TryGetValue(q, out var v) ? v : Array.Empty<long>();
        }
        public IReadOnlyList<long> GetSubclassOf(long q)
        {
            SubclassCalls++;
            if (ThrowSubclassFor == q) throw new InvalidOperationException("boom-subclass");
            return Parents.TryGetValue(q, out var v) ? v : Array.Empty<long>();
        }
    }

    private static G2Concept C(long q, string src = "P31Degree1") => new(q, src);

    private static long[] Structural(FakeAdapter a, long concept)
    {
        var set = new SortedSet<long>();
        foreach (long tg in a.Instance.TryGetValue(concept, out var targets) ? targets.OrderBy(x => x) : Enumerable.Empty<long>())
        {
            set.Add(tg);
            var t = GraphTraversal.Ancestry(tg, 3, 5000, q => a.Parents.TryGetValue(q, out var p) ? p.OrderBy(x => x).ToArray() : Array.Empty<long>());
            foreach (long d in t.Discovered) set.Add(d);
        }
        return set.ToArray();
    }

    private static G2Workload BuildExpected(FakeAdapter a, IReadOnlyList<G2Concept> concepts)
    {
        var per = concepts.Select(c => new G2PerInputExpected(0, c.Qid, c.SourceStratum, 0, ""));
        var rows = concepts.Select(c => (c.Qid, Structural(a, c.Qid))).ToList();
        var list = new List<G2PerInputExpected>();
        for (int i = 0; i < rows.Count; i++)
        {
            var set = rows[i].Item2;
            list.Add(new G2PerInputExpected(i, rows[i].Item1, concepts[i].SourceStratum, set.Length,
                WorkloadOracle.StructuralSetDigest(set)));
        }
        return new G2Workload
        {
            Concepts = concepts,
            PerInput = list,
            Batch = new G2BatchExpected(rows.Count, WorkloadOracle.G2BatchDigest(rows)),
        };
    }

    [Fact]
    public void Executor_DuplicateTargets_NotDedupedBeforeTraversal()
    {
        var a = new FakeAdapter { Instance = new Dictionary<long, long[]> { [1] = new[] { 5L, 5L } } };
        var exec = new G2OperationExecutor(a);
        var outcomes = exec.Execute(new[] { C(1) });
        Assert.Single(outcomes);
        Assert.Equal(new[] { 5L }, outcomes[0].StructuralQidsAscending);
        Assert.Equal(2, a.SubclassCalls); // each duplicate occurrence traversed
    }

    [Fact]
    public void Executor_UnsortedMultiTargets_ProcessedAscending()
    {
        var a = new FakeAdapter { Instance = new Dictionary<long, long[]> { [1] = new[] { 30L, 10L, 20L } } };
        var seen = new List<long>();
        var adapter = new CallRecordingAdapter(a, seen);
        new G2OperationExecutor(adapter).Execute(new[] { C(1) });
        Assert.Equal(new[] { 10L, 20L, 30L }, seen);
    }

    private sealed class CallRecordingAdapter : IStorageCandidate
    {
        private readonly FakeAdapter _inner;
        private readonly List<long> _seen;
        public CallRecordingAdapter(FakeAdapter inner, List<long> seen) { _inner = inner; _seen = seen; }
        public void Open() => _inner.Open();
        public void Dispose() => _inner.Dispose();
        public ConceptHit GetConcept(long q) => _inner.GetConcept(q);
        public IReadOnlyList<LexicalHit> LookupLexical(string l, string v) => _inner.LookupLexical(l, v);
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long q) => _inner.GetLexicalByQid(q);
        public IReadOnlyList<long> GetInstanceOf(long q) => _inner.GetInstanceOf(q);
        public IReadOnlyList<long> GetSubclassOf(long q) { _seen.Add(q); return _inner.GetSubclassOf(q); }
    }

    [Fact]
    public void Runner_CorrectBatch_Valid_AndPerInputValid()
    {
        var a = new FakeAdapter
        {
            Instance = new Dictionary<long, long[]> { [1] = new[] { 10L }, [2] = new[] { 20L, 30L } },
            Parents = new Dictionary<long, long[]> { [10] = new[] { 100L }, [20] = Array.Empty<long>(), [30] = new[] { 10L } },
        };
        var concepts = new[] { C(1), C(2, "P31Degree2Plus") };
        var w = BuildExpected(a, concepts);
        var (per, batch) = new G2CorrectnessRunner(a).RunAll(w);
        Assert.All(per, r => Assert.Equal(ServingStatuses.Valid, r.Status));
        Assert.Equal(ServingStatuses.Valid, batch.Status);
        Assert.Equal(0, a.OpenCalls);
    }

    [Fact]
    public void Runner_BadPerInput_PreventsBatchValid()
    {
        var a = new FakeAdapter { Instance = new Dictionary<long, long[]> { [1] = new[] { 10L } } };
        var concepts = new[] { C(1) };
        var w = BuildExpected(a, concepts);
        // Wrong per-input digest.
        w = new G2Workload
        {
            Concepts = w.Concepts,
            PerInput = new[] { w.PerInput[0] with { Digest = "0".PadRight(64, '0') } },
            Batch = w.Batch,
        };
        var (per, batch) = new G2CorrectnessRunner(a).RunAll(w);
        Assert.Equal(ServingStatuses.Invalid, per[0].Status);
        Assert.Equal(ServingStatuses.Invalid, batch.Status);
    }

    [Fact]
    public void Runner_OneExecutionError_BatchError_AndContinuation()
    {
        var a = new FakeAdapter
        {
            Instance = new Dictionary<long, long[]> { [1] = new[] { 10L }, [2] = new[] { 20L } },
            ThrowInstanceFor = 1,
        };
        var concepts = new[] { C(1), C(2) };
        var w = BuildExpected(a, concepts);
        var (per, batch) = new G2CorrectnessRunner(a).RunAll(w);
        Assert.Equal(ServingStatuses.Error, per[0].Status);
        Assert.Equal(ServingStatuses.Valid, per[1].Status); // continued after error
        Assert.Equal(ServingStatuses.Error, batch.Status);
    }

    [Fact]
    public void Executor_GuardExceed_InputError()
    {
        var a = new FakeAdapter { Instance = new Dictionary<long, long[]> { [1] = new[] { 10L } } };
        // 10 -> chain deeper than guard? use direct guard test via huge parent set on target 10.
        a.Parents[10] = Enumerable.Range(1000, 6000).Select(i => (long)i).ToArray();
        var outcomes = new G2OperationExecutor(a).Execute(new[] { C(1) });
        Assert.NotNull(outcomes[0].ErrorMessage);
        Assert.Null(outcomes[0].StructuralQidsAscending);
    }

    [Fact]
    public void Executor_SubclassException_InputError_NextContinues()
    {
        var a = new FakeAdapter
        {
            Instance = new Dictionary<long, long[]> { [1] = new[] { 10L }, [2] = new[] { 20L } },
            ThrowSubclassFor = 10,
        };
        var outcomes = new G2OperationExecutor(a).Execute(new[] { C(1), C(2) });
        Assert.NotNull(outcomes[0].ErrorMessage);
        Assert.NotNull(outcomes[1].StructuralQidsAscending);
    }

    [Fact]
    public void Executor_OverlappingTargets_IndependentTraversalState()
    {
        var a = new FakeAdapter
        {
            Instance = new Dictionary<long, long[]> { [1] = new[] { 10L, 20L } },
            Parents = new Dictionary<long, long[]> { [10] = new[] { 30L }, [20] = new[] { 30L } },
        };
        var outcomes = new G2OperationExecutor(a).Execute(new[] { C(1) });
        Assert.Equal(new long[] { 10, 20, 30 }, outcomes[0].StructuralQidsAscending);
    }
}

using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class A1RunnerTests
{
    private sealed class FakeAnalytical : IAnalyticalCandidate
    {
        public List<ConceptRow> Concepts { get; set; } = new();
        public List<LexicalRow> Lexical { get; set; } = new();
        public List<EdgeRow> Instance { get; set; } = new();
        public List<EdgeRow> Subclass { get; set; } = new();
        public int OpenCalls { get; private set; }
        public bool ThrowOnConcept { get; set; }
        public bool ThrowDeferredConcept { get; set; }

        public void Open() => OpenCalls++;
        public void Dispose() { }

        public IEnumerable<ConceptRow> ScanConcept()
        {
            if (ThrowOnConcept) throw new InvalidOperationException("boom");
            foreach (var c in Concepts)
            {
                if (ThrowDeferredConcept && c.Qid == Concepts[^1].Qid) throw new InvalidOperationException("deferred");
                yield return c;
            }
        }

        public IEnumerable<LexicalRow> ScanLexicalEntry() => Lexical;
        public IEnumerable<EdgeRow> ScanInstanceOf() => Instance;
        public IEnumerable<EdgeRow> ScanSubclassOf() => Subclass;
        public IReadOnlyList<(string Lang, string LexKind, long Count)> A2LangKindCounts() => throw new NotSupportedException();
        public IReadOnlyList<(long TargetQid, long Count)> A3P31Fanout() => throw new NotSupportedException();
        public IReadOnlyList<(long TargetQid, long Count)> A4P279Fanout() => throw new NotSupportedException();
        public IReadOnlyList<A5Row> A5P31TargetLabels() => throw new NotSupportedException();
    }

    private static (long Count, string Digest) Fold(IEnumerable<byte[]> rows)
    {
        var fold = new MultisetFoldV1();
        foreach (var r in rows) fold.Add(r);
        return (fold.Count, fold.Digest());
    }

    private static AnalyticalWorkload ExpectedFor(FakeAnalytical a)
    {
        var (cc, cd) = Fold(a.Concepts.Select(c => MultisetFoldV1.ConceptRow(c.Qid, c.InT1, c.InT2)));
        var (lc, ld) = Fold(a.Lexical.Select(r => MultisetFoldV1.LexicalRow(r.Qid, r.Lang, r.LexKind, r.Value)));
        var (ic, idg) = Fold(a.Instance.Select(e => MultisetFoldV1.EdgeRow(e.SubjectQid, e.TargetQid)));
        var (sc, sd) = Fold(a.Subclass.Select(e => MultisetFoldV1.EdgeRow(e.SubjectQid, e.TargetQid)));
        return new AnalyticalWorkload
        {
            Expected = new Dictionary<string, A1Expected>(StringComparer.Ordinal)
            {
                ["A1-Concept"] = new("A1-Concept", cc, cd),
                ["A1-LexicalEntry"] = new("A1-LexicalEntry", lc, ld),
                ["A1-InstanceOf"] = new("A1-InstanceOf", ic, idg),
                ["A1-SubclassOf"] = new("A1-SubclassOf", sc, sd),
            },
        };
    }

    private static FakeAnalytical Sample()
    {
        return new FakeAnalytical
        {
            Concepts = new List<ConceptRow> { new(1, true, false), new(2, true, false), new(2, true, false) },
            Lexical = new List<LexicalRow> { new(1, "en", "label", "Alpha"), new(1, "en", "label", "alpha") },
            Instance = new List<EdgeRow> { new(1, 5), new(1, 5) },
            Subclass = new List<EdgeRow> { new(1, 10), new(2, 20) },
        };
    }

    [Fact]
    public void CorrectScans_Valid_AndNoOpen()
    {
        var a = Sample();
        var results = new A1CorrectnessRunner(a).RunAll(ExpectedFor(a));
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(ServingStatuses.Valid, r.Status));
        Assert.Equal(0, a.OpenCalls);
    }

    [Fact]
    public void DuplicatesAffectCount_AndOrderIrrelevant()
    {
        var a = Sample();
        var expected = ExpectedFor(a);
        var exec = new A1OperationExecutor(a);
        var concept = exec.Execute("A1-Concept");
        Assert.Equal(3L, concept.ActualRowCount); // Qid 2 duplicated
        // Reversed scan order yields same digest.
        var rev = new FakeAnalytical { Concepts = new List<ConceptRow> { new(2, true, false), new(2, true, false), new(1, true, false) } };
        Assert.Equal(new A1OperationExecutor(rev).Execute("A1-Concept").ActualDigest, concept.ActualDigest);
        Assert.Equal(expected.Expected["A1-Concept"].Digest, concept.ActualDigest);
    }

    [Fact]
    public void Mismatches_Invalid_MissingExtraAndMutation()
    {
        var full = Sample();
        var subset = new FakeAnalytical { Concepts = full.Concepts.Skip(1).ToList() };
        var runnerSub = new A1CorrectnessRunner(subset);
        Assert.Equal(ServingStatuses.Invalid, runnerSub.RunAll(ExpectedFor(full))[0].Status); // missing row

        var runnerFull = new A1CorrectnessRunner(full);
        Assert.Equal(ServingStatuses.Invalid, runnerFull.RunAll(ExpectedFor(subset))[0].Status); // extra duplicate

        var mutated = new FakeAnalytical { Concepts = full.Concepts, Lexical = new List<LexicalRow> { new(1, "en", "label", "Other") } };
        Assert.Equal(ServingStatuses.Invalid, new A1CorrectnessRunner(mutated).RunAll(ExpectedFor(full))[1].Status); // field mutation
    }

    [Fact]
    public void Exception_Error_AndDeferredException_Error_AndContinuation()
    {
        var boom = new FakeAnalytical { Concepts = Sample().Concepts, ThrowOnConcept = true };
        var good = Sample();
        var results = new A1CorrectnessRunner(boom).RunAll(ExpectedFor(boom));
        Assert.Equal(ServingStatuses.Error, results[0].Status); // Concept throws
        Assert.Equal(ServingStatuses.Valid, results[1].Status); // Lexical still runs

        var deferred = new FakeAnalytical { Concepts = Sample().Concepts, ThrowDeferredConcept = true };
        var r2 = new A1CorrectnessRunner(deferred).RunAll(ExpectedFor(deferred));
        Assert.Equal(ServingStatuses.Error, r2[0].Status);
    }
}

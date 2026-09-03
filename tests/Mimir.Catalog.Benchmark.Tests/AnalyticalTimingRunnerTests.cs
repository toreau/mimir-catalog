using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class AnalyticalTimingRunnerTests
{
    private sealed class FakeCandidate : IAnalyticalCandidate
    {
        public List<ConceptRow> Concepts { get; init; } = new();
        public List<LexicalRow> Lexical { get; init; } = new();
        public List<EdgeRow> Instance { get; init; } = new();
        public List<EdgeRow> Subclass { get; init; } = new();
        public List<(string Lang, string LexKind, long Count)> A2Rows { get; init; } = new();
        public List<(long TargetQid, long Count)> A3Rows { get; init; } = new();
        public List<(long TargetQid, long Count)> A4Rows { get; init; } = new();
        public List<A5Row> A5Rows { get; init; } = new();

        /// <summary>Throw at the given per-instance invocation ordinal (warmup=1, timed=2) per op.</summary>
        public Dictionary<string, int> ThrowAt { get; init; } = new();

        public List<string> Log { get; } = new();
        public int OpenCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        private readonly Dictionary<string, int> _calls = new();

        private void Enter(string op)
        {
            Log.Add(op);
            int n = _calls.GetValueOrDefault(op) + 1;
            _calls[op] = n;
            if (ThrowAt.TryGetValue(op, out int at) && n == at)
                throw new InvalidOperationException($"boom {op} call {n}");
        }

        public void Open() => OpenCalls++;
        public void Dispose() => DisposeCalls++;

        public IEnumerable<ConceptRow> ScanConcept() { Enter("A1-Concept"); return Concepts; }
        public IEnumerable<LexicalRow> ScanLexicalEntry() { Enter("A1-LexicalEntry"); return Lexical; }
        public IEnumerable<EdgeRow> ScanInstanceOf() { Enter("A1-InstanceOf"); return Instance; }
        public IEnumerable<EdgeRow> ScanSubclassOf() { Enter("A1-SubclassOf"); return Subclass; }
        public IReadOnlyList<(string Lang, string LexKind, long Count)> A2LangKindCounts() { Enter("A2"); return A2Rows; }
        public IReadOnlyList<(long TargetQid, long Count)> A3P31Fanout() { Enter("A3"); return A3Rows; }
        public IReadOnlyList<(long TargetQid, long Count)> A4P279Fanout() { Enter("A4"); return A4Rows; }
        public IReadOnlyList<A5Row> A5P31TargetLabels() { Enter("A5"); return A5Rows; }
    }

    private static readonly string[] Ops =
        ["A1-Concept", "A1-LexicalEntry", "A1-InstanceOf", "A1-SubclassOf", "A2", "A3", "A4", "A5"];

    private static FakeCandidate Sample()
    {
        return new FakeCandidate
        {
            Concepts = new List<ConceptRow> { new(1, true, false), new(2, true, false) },
            Lexical = new List<LexicalRow> { new(1, "en", "label", "Alpha") },
            Instance = new List<EdgeRow> { new(1, 5), new(1, 5) },
            Subclass = new List<EdgeRow> { new(1, 10) },
            A2Rows = new List<(string, string, long)> { ("en", "label", 1) },
            A3Rows = new List<(long, long)> { (5, 2) },
            A4Rows = new List<(long, long)> { (10, 1) },
            A5Rows = new List<A5Row> { new(5, 2, "Alpha", null) },
        };
    }

    private static AnalyticalWorkload WorkloadFor(FakeCandidate c)
    {
        var exp = new Dictionary<string, A1Expected>(StringComparer.Ordinal);
        static (long, string) Fold(IEnumerable<byte[]> rows)
        {
            var f = new MultisetFoldV1();
            foreach (var r in rows) f.Add(r);
            return (f.Count, f.Digest());
        }
        var (cc, cd) = Fold(c.Concepts.Select(r => MultisetFoldV1.ConceptRow(r.Qid, r.InT1, r.InT2)));
        var (lc, ld) = Fold(c.Lexical.Select(r => MultisetFoldV1.LexicalRow(r.Qid, r.Lang, r.LexKind, r.Value)));
        var (ic, id) = Fold(c.Instance.Select(r => MultisetFoldV1.EdgeRow(r.SubjectQid, r.TargetQid)));
        var (sc, sd) = Fold(c.Subclass.Select(r => MultisetFoldV1.EdgeRow(r.SubjectQid, r.TargetQid)));
        exp["A1-Concept"] = new("A1-Concept", cc, cd);
        exp["A1-LexicalEntry"] = new("A1-LexicalEntry", lc, ld);
        exp["A1-InstanceOf"] = new("A1-InstanceOf", ic, id);
        exp["A1-SubclassOf"] = new("A1-SubclassOf", sc, sd);
        var (a2c, a2d) = (c.A2Rows.Count, WorkloadOracle.AnalyticalRowsDigest(
            c.A2Rows.OrderBy(r => r.Lang, StringComparer.Ordinal).ThenBy(r => r.LexKind, StringComparer.Ordinal)
                .Select(r => WorkloadOracle.LangKindCountRow(r.Lang, r.LexKind, r.Count)).ToArray()));
        exp["A2"] = new("A2", a2c, a2d);
        var (a3c, a3d) = (c.A3Rows.Count, WorkloadOracle.AnalyticalRowsDigest(
            c.A3Rows.OrderBy(r => r.TargetQid).Select(r => WorkloadOracle.TargetCountRow(r.TargetQid, r.Count)).ToArray()));
        exp["A3"] = new("A3", a3c, a3d);
        var (a4c, a4d) = (c.A4Rows.Count, WorkloadOracle.AnalyticalRowsDigest(
            c.A4Rows.OrderBy(r => r.TargetQid).Select(r => WorkloadOracle.TargetCountRow(r.TargetQid, r.Count)).ToArray()));
        exp["A4"] = new("A4", a4c, a4d);
        var (a5c, a5d) = (c.A5Rows.Count, WorkloadOracle.AnalyticalRowsDigest(
            c.A5Rows.OrderBy(r => r.TargetQid).Select(r => WorkloadOracle.A5Row(r.TargetQid, r.Fanout, r.EnLabel, r.NbLabel)).ToArray()));
        exp["A5"] = new("A5", a5c, a5d);
        return new AnalyticalWorkload { Expected = exp };
    }

    private static double[] Durations(int reps) => Enumerable.Repeat(0.0, Ops.Length * reps).ToArray();

    [Fact]
    public void FullSuccess_3FreshCandidates_24Samples_ValidSummaries()
    {
        var instances = new List<FakeCandidate>();
        var sample = Sample();
        var runner = new AnalyticalTimingRunner(
            () => { var c = Sample(); instances.Add(c); return c; },
            WorkloadFor(sample),
            () => new ScriptedTimer(Durations(3)));
        var r = runner.Run();
        Assert.Equal(3, instances.Count);
        Assert.All(instances, c => { Assert.Equal(1, c.OpenCalls); Assert.Equal(1, c.DisposeCalls); });
        Assert.Empty(r.WarmupFailures);
        Assert.Equal(24, r.Samples.Count);
        Assert.All(r.Samples, s => Assert.Equal(TimedResultStatus.Valid, s.Status));
        Assert.Equal(3, r.Samples.Count(s => s.Operation == "A5"));
        Assert.Equal(24, r.Samples.Select(s => (s.Operation, s.Repetition)).Distinct().Count());
        Assert.All(r.Summaries, s => Assert.Equal(AnalyticalSummaryStatus.Valid, s.Status));
        Assert.NotNull(r.Summaries[0].MedianSeconds);
    }

    [Fact]
    public void ExactWarmupThenTimedOrder_PerRepetition()
    {
        var instances = new List<FakeCandidate>();
        var runner = new AnalyticalTimingRunner(
            () => { var c = Sample(); instances.Add(c); return c; },
            WorkloadFor(Sample()),
            () => new ScriptedTimer(Durations(3)));
        runner.Run();
        foreach (var c in instances)
        {
            Assert.Equal(16, c.Log.Count); // 8 warmup + 8 timed
            Assert.Equal(Ops, c.Log.Take(8).ToArray());
            Assert.Equal(Ops, c.Log.Skip(8).ToArray());
        }
    }

    [Fact]
    public void MedianOfThree_ReusesWorkloadMetrics()
    {
        var durations = new List<double>();
        // per rep, op wall values: rep1=1.0, rep2=2.0, rep3=3.0 for every op
        for (int rep = 0; rep < 3; rep++)
            foreach (var _ in Ops)
                durations.Add(rep == 2 ? 3.0 : rep == 1 ? 2.0 : 1.0);
        var script = new ScriptedTimer(durations);
        var runner = new AnalyticalTimingRunner(
            () => Sample(),
            WorkloadFor(Sample()),
            () => script);
        var r = runner.Run();
        var a5 = r.Summaries.Single(s => s.Operation == "A5");
        Assert.Equal(2.0, a5.MedianSeconds!.Value, 6);
        var a3 = r.Summaries.Single(s => s.Operation == "A3");
        Assert.Equal(2.0, a3.MedianSeconds!.Value, 6);
    }

    [Fact]
    public void InvalidSample_Retained_SummaryIncomplete()
    {
        var sample = Sample();
        var workload = WorkloadFor(sample);
        var tampered = new Dictionary<string, A1Expected>(workload.Expected, StringComparer.Ordinal)
        {
            ["A2"] = workload.Expected["A2"] with { Cardinality = 999 },
        };
        workload = new AnalyticalWorkload { Expected = tampered };
        var runner = new AnalyticalTimingRunner(() => Sample(), workload, () => new ScriptedTimer(Durations(3)));
        var r = runner.Run();
        Assert.Equal(3, r.Samples.Count(s => s.Operation == "A2" && s.Status == TimedResultStatus.Invalid));
        var a2 = r.Summaries.Single(s => s.Operation == "A2");
        Assert.Equal(AnalyticalSummaryStatus.Incomplete, a2.Status);
        Assert.Null(a2.MedianSeconds);
    }

    [Fact]
    public void TimedError_ContinuesLaterOpsAndRepetitions()
    {
        // A3 throws on its second invocation (timed pass) in repetition 1 only.
        int made = 0;
        var runner = new AnalyticalTimingRunner(
            () =>
            {
                var c = Sample();
                if (made == 0) c.ThrowAt["A3"] = 2;
                made++;
                return c;
            },
            WorkloadFor(Sample()),
            () => new ScriptedTimer(Durations(3)));
        var r = runner.Run();
        var rep1A3 = r.Samples.Where(s => s.Repetition == 1 && s.Operation == "A3").Single();
        Assert.Equal(TimedResultStatus.Error, rep1A3.Status);
        Assert.NotNull(rep1A3.ErrorMessage);
        // A4/A5 in same repetition still ran (later ops continue).
        Assert.Equal(TimedResultStatus.Valid, r.Samples.Single(s => s.Repetition == 1 && s.Operation == "A4").Status);
        Assert.Equal(TimedResultStatus.Valid, r.Samples.Single(s => s.Repetition == 1 && s.Operation == "A5").Status);
        // Later repetitions unaffected and A3 summary incomplete.
        Assert.Equal(2, r.Samples.Count(s => s.Repetition > 1 && s.Operation == "A3" && s.Status == TimedResultStatus.Valid));
        Assert.Equal(AnalyticalSummaryStatus.Incomplete, r.Summaries.Single(s => s.Operation == "A3").Status);
    }

    [Fact]
    public void WarmupError_BlocksOnlyThatRepetition()
    {
        int made = 0;
        var runner = new AnalyticalTimingRunner(
            () =>
            {
                var c = Sample();
                if (made == 0) c.ThrowAt["A5"] = 1; // throw during warmup of rep 1
                made++;
                return c;
            },
            WorkloadFor(Sample()),
            () => new ScriptedTimer(Durations(3)));
        var r = runner.Run();
        Assert.Single(r.WarmupFailures);
        Assert.Equal(1, r.WarmupFailures[0].Repetition);
        Assert.Equal("A5", r.WarmupFailures[0].Operation);
        Assert.Equal(16, r.Samples.Count); // reps 2+3 only
        Assert.DoesNotContain(r.Samples, s => s.Repetition == 1);
    }

    [Fact]
    public void OneOrTwoValid_NoAuthoritativeMedian()
    {
        int made = 0;
        var runner = new AnalyticalTimingRunner(
            () =>
            {
                var c = Sample();
                if (made == 2) c.ThrowAt["A2"] = 2; // timed error rep 3
                made++;
                return c;
            },
            WorkloadFor(Sample()),
            () => new ScriptedTimer(Durations(3)));
        var r = runner.Run();
        var a2 = r.Summaries.Single(s => s.Operation == "A2");
        Assert.Equal(2, r.Samples.Count(s => s.Operation == "A2" && s.Status == TimedResultStatus.Valid));
        Assert.Equal(AnalyticalSummaryStatus.Incomplete, a2.Status);
        Assert.Null(a2.MedianSeconds);
    }

    [Fact]
    public void RepetitionLabels_AreOneTwoThree()
    {
        var runner = new AnalyticalTimingRunner(() => Sample(), WorkloadFor(Sample()), () => new ScriptedTimer(Durations(3)));
        var r = runner.Run();
        Assert.Equal(new[] { 1, 2, 3 }, r.Samples.Select(s => s.Repetition).Distinct().OrderBy(x => x).ToArray());
        Assert.All(r.Samples, s => Assert.Contains(s.Repetition, new[] { 1, 2, 3 }));
    }


// ---------- boundary observability ----------

private sealed class ObsList<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _inner;
    private readonly Action<string> _onEnumerate;
    private readonly string _tag;

    public ObsList(IReadOnlyList<T> inner, Action<string> onEnumerate, string tag)
    { _inner = inner; _onEnumerate = onEnumerate; _tag = tag; }

    public T this[int index] => _inner[index];
    public int Count => _inner.Count;
    public IEnumerator<T> GetEnumerator() { _onEnumerate("enum:" + _tag); return _inner.GetEnumerator(); }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

private sealed class RecordingCandidate : IAnalyticalCandidate
{
    private readonly List<string> _rec;
    private readonly Dictionary<string, int> _throwAt;
    private readonly Dictionary<string, int> _calls = new();
    private readonly List<ConceptRow> _concepts;
    private readonly List<LexicalRow> _lexical;
    private readonly List<EdgeRow> _instance;
    private readonly List<EdgeRow> _subclass;
    private readonly List<(string, string, long)> _a2;
    private readonly List<(long, long)> _a3;
    private readonly List<(long, long)> _a4;
    private readonly List<A5Row> _a5;
    public bool ThrowOnOpen { get; init; }
    public int OpenCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    public RecordingCandidate(List<string> rec, Dictionary<string, int>? throwAt = null)
    {
        _rec = rec; _throwAt = throwAt ?? new();
        _concepts = new(); _lexical = new(); _instance = new(); _subclass = new();
        _a2 = new(); _a3 = new(); _a4 = new(); _a5 = new();
    }

    private void Enter(string op)
    {
        _rec.Add("exec:" + op);
        int n = _calls.GetValueOrDefault(op) + 1;
        _calls[op] = n;
        if (_throwAt.TryGetValue(op, out int at) && n == at) throw new InvalidOperationException("boom " + op);
    }

    private Action<string> Enum => _rec.Add;

    public void Open() { if (ThrowOnOpen) throw new InvalidOperationException("boom open"); OpenCalls++; _rec.Add("open"); }
    public void Dispose() { DisposeCalls++; _rec.Add("dispose"); }

    public IEnumerable<ConceptRow> ScanConcept() { Enter("A1-Concept"); return new ObsList<ConceptRow>(_concepts, Enum, "A1-Concept"); }
    public IEnumerable<LexicalRow> ScanLexicalEntry() { Enter("A1-LexicalEntry"); return new ObsList<LexicalRow>(_lexical, Enum, "A1-LexicalEntry"); }
    public IEnumerable<EdgeRow> ScanInstanceOf() { Enter("A1-InstanceOf"); return new ObsList<EdgeRow>(_instance, Enum, "A1-InstanceOf"); }
    public IEnumerable<EdgeRow> ScanSubclassOf() { Enter("A1-SubclassOf"); return new ObsList<EdgeRow>(_subclass, Enum, "A1-SubclassOf"); }
    public IReadOnlyList<(string Lang, string LexKind, long Count)> A2LangKindCounts() { Enter("A2"); return new ObsList<(string, string, long)>(_a2, Enum, "A2"); }
    public IReadOnlyList<(long TargetQid, long Count)> A3P31Fanout() { Enter("A3"); return new ObsList<(long, long)>(_a3, Enum, "A3"); }
    public IReadOnlyList<(long TargetQid, long Count)> A4P279Fanout() { Enter("A4"); return new ObsList<(long, long)>(_a4, Enum, "A4"); }
    public IReadOnlyList<A5Row> A5P31TargetLabels() { Enter("A5"); return new ObsList<A5Row>(_a5, Enum, "A5"); }
}

private sealed class RecordingTimer : ITimer
{
    private readonly ScriptedTimer _inner;
    private readonly List<string> _rec;
    public RecordingTimer(ScriptedTimer inner, List<string> rec) { _inner = inner; _rec = rec; }
    public void Start() => _inner.Start();
    public double StopSeconds() { double v = _inner.StopSeconds(); _rec.Add("stop"); return v; }
}

private static AnalyticalWorkload DummyWorkload()
{
    var exp = new Dictionary<string, A1Expected>(StringComparer.Ordinal);
    foreach (var op in Ops) exp[op] = new A1Expected(op, 0, "");
    return new AnalyticalWorkload { Expected = exp };
}

private static (List<string> Rec, IReadOnlyList<AnalyticalTimedSample> Samples) RunInstrumented(
    List<string> rec, IReadOnlyList<double> durations, Dictionary<string, int>? rep1Throw = null)
{
    var script = new ScriptedTimer(durations);
    int made = 0;
    var runner = new AnalyticalTimingRunner(
        () =>
        {
            var c = new RecordingCandidate(rec, made == 0 ? rep1Throw : null) { ThrowOnOpen = made != 0 };
            made++;
            return c;
        },
        DummyWorkload(),
        () => new RecordingTimer(script, rec));
    return (rec, runner.Run().Samples);
}

private static int Second(string[] events, string tag)
{
    int count = 0;
    for (int i = 0; i < events.Length; i++)
    {
        if (events[i] == tag) { count++; if (count == 2) return i; }
    }
    return -1;
}

[Fact]
public void Boundary_TimerStopsBeforeCanonicalization_A2A5()
{
    var rec = new List<string>();
    var (events, _) = RunInstrumented(rec, Enumerable.Repeat(0.1, 24).ToList());
    string[] e = events.ToArray();
    foreach (var op in new[] { "A2", "A3", "A4", "A5" })
    {
        int exec2 = Second(e, "exec:" + op);
        Assert.True(exec2 >= 0, "timed exec " + op + " missing");
        Assert.Equal("stop", e[exec2 + 1]);
        Assert.Contains("enum:" + op, e.Skip(exec2 + 2));
    }
}

[Fact]
public void Boundary_A1ExecutorFoldInsideTimer_CompareWithoutEnumeration()
{
    var rec = new List<string>();
    var (events, _) = RunInstrumented(rec, Enumerable.Repeat(0.1, 24).ToList());
    string[] e = events.ToArray();
    int exec2 = Second(e, "exec:A1-Concept");
    // executor enumerates relation while timer runs, then timer stops.
    Assert.Equal("enum:A1-Concept", e[exec2 + 1]);
    Assert.Equal("stop", e[exec2 + 2]);
    // classification after stop must not enumerate the relation again.
    for (int i = exec2 + 3; i < e.Length; i++)
    {
        if (e[i].StartsWith("exec:", StringComparison.Ordinal)) break;
        Assert.NotEqual("enum:A1-Concept", e[i]);
    }
}

[Fact]
public void Boundary_MeasuredException_StillStopsTimer_RetainsWall_NoCanonical()
{
    var rec = new List<string>();
    var durations = Enumerable.Repeat(0.1, 24).ToList();
    durations[7] = 1.5; // A5 is the eighth timed operation of repetition 1
    var (events, samples) = RunInstrumented(rec, durations, rep1Throw: new Dictionary<string, int> { ["A5"] = 2 });
    var a5 = samples.Single(s => s.Repetition == 1 && s.Operation == "A5");
    Assert.Equal(TimedResultStatus.Error, a5.Status);
    Assert.Equal(1.5, a5.WallSeconds, 6);
    string[] e = events.ToArray();
    int exec2 = Second(e, "exec:A5");
    Assert.Equal("stop", e[exec2 + 1]);
    Assert.DoesNotContain("enum:A5", e.Skip(exec2 + 2));
    Assert.NotNull(a5.ErrorMessage);
}

}

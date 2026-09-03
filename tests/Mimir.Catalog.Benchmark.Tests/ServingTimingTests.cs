using System.Text;
using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class ServingTimingTests
{
    private sealed class FakeServingCandidate : IStorageCandidate
    {
        public Dictionary<long, ConceptHit> Concepts { get; } = new();
        public List<string> ConceptLog { get; } = new();
        public long? ThrowOnSecondCallQid { get; set; }
        public long? ThrowOnFirstCallQid { get; set; }
        public long? WrongOnSecondCallQid { get; set; }

        public void Open() { }
        public void Dispose() { }

        public ConceptHit GetConcept(long qid)
        {
            ConceptLog.Add("concept:" + qid);
            int call = ConceptLog.Count(x => x == "concept:" + qid);
            if (ThrowOnFirstCallQid == qid && call == 1) throw new InvalidOperationException("boom first call");
            if (ThrowOnSecondCallQid == qid && call == 2) throw new InvalidOperationException("boom second call");
            var hit = Concepts[qid];
            if (WrongOnSecondCallQid == qid && call == 2)
                return new ConceptHit(Present: false, InT1: hit.InT1, InT2: hit.InT2);
            return hit;
        }

        public IReadOnlyList<LexicalHit> LookupLexical(string lang, string value) => Array.Empty<LexicalHit>();
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long qid) => Array.Empty<LexicalRow>();
        public IReadOnlyList<long> GetInstanceOf(long subjectQid) => Array.Empty<long>();
        public IReadOnlyList<long> GetSubclassOf(long subjectQid) => Array.Empty<long>();
    }

    private static ServingProbe P(string op, long seq, string stratum = "Hit", bool measured = true, long qid = 0)
        => new(op, seq, stratum, measured, qid, null, null);

    private static ServingWorkload S1Workload(long[] qids, long[]? tailQids = null,
        Action<Dictionary<(string, long), ServingExpected>>? tamper = null)
    {
        var probes = new List<ServingProbe>();
        long seq = 1;
        foreach (var qid in qids)
            probes.Add(P("S1", seq++, "Hit", true, qid));
        long tseq = 500;
        foreach (var qid in tailQids ?? Array.Empty<long>())
            probes.Add(P("S1", tseq++, "Tail", false, qid));

        var expected = new Dictionary<(string, long), ServingExpected>();
        foreach (var p in probes)
        {
            // candidates expose Present=true, InT1=true, InT2=false for every qid
            long card = p.Qid == 0 ? 0 : 1;
            string digest = WorkloadOracle.ConceptResultDigest(p.Qid!.Value, true, true, false);
            expected[(p.Op, p.Seq)] = new ServingExpected(p.Op, p.Seq, p.Measured, card, digest);
        }
        tamper?.Invoke(expected);
        return new ServingWorkload { Probes = probes, Expected = expected };
    }

    private static (FakeServingCandidate Candidate, ServingTimingRunner Runner) RunSetup(
        long[] qids, Action<FakeServingCandidate>? configure = null, long[]? tails = null)
    {
        var candidate = new FakeServingCandidate();
        foreach (var qid in qids.Concat(tails ?? Array.Empty<long>()))
            candidate.Concepts[qid] = new ConceptHit(true, true, false);
        configure?.Invoke(candidate);
        var workload = S1Workload(qids, tails);
        var script = new ScriptedTimer(new double[] { 0.5, 1.5, 6.0 });
        var runner = new ServingTimingRunner(candidate, workload, "S1", 1, () => script);
        return (candidate, runner);
    }

    [Fact]
    public void Selection_PerOp_MeasuredOnly_PreservesOrder()
    {
        var probes = new[]
        {
            P("S2", 5, measured: false, qid: 0),
            P("S1", 1, qid: 10),
            P("S1", 2, qid: 20),
            P("S2", 3, qid: 0),
            P("S1", 4, measured: false, qid: 30),
            P("S1", 3, qid: 30),
        };
        var s1 = ServingTimingRunner.Select(probes, "S1", measuredOnly: true);
        Assert.Equal(new long[] { 1, 2, 3 }, s1.Select(p => p.Seq));
        Assert.All(s1, p => Assert.True(p.Measured));
        Assert.Equal(new long[] { 4 }, ServingTimingRunner.Select(probes, "S1", measuredOnly: false).Select(p => p.Seq));
    }

    [Fact]
    public void CorrectRun_WarmupThenTimed_OnceEach_WallsRetained()
    {
        long[] qids = { 100, 200, 300 };
        var (candidate, runner) = RunSetup(qids);
        var exec = runner.Execute();

        Assert.Equal(ServingStatuses.Valid, exec.Correctness);
        Assert.Equal(3, exec.Samples.Count);
        Assert.All(exec.Samples, s => Assert.Equal(ServingStatuses.Valid, s.CorrectnessStatus));
        Assert.Equal(new double[] { 0.5, 1.5, 6.0 }, exec.Samples.Select(s => s.WallSeconds));
        // 6.0 seconds retained, no reclassification
        Assert.Equal(6.0, exec.Samples[2].WallSeconds);

        // warmup calls (3) + timed calls (3), no further retrieval for canonicalization
        Assert.Equal(6, candidate.ConceptLog.Count);
        Assert.Equal(2, candidate.ConceptLog.Count(x => x == "concept:100"));
        Assert.NotNull(exec.TimedPassWallSeconds);
    }

    [Fact]
    public void WarmupInvalid_BlocksTimed_NoSamples()
    {
        long[] qids = { 100, 200, 300 };
        var candidate = new FakeServingCandidate();
        foreach (var q in qids) candidate.Concepts[q] = new ConceptHit(true, true, false);
        // Expected cardinality is tampered to 99 for seq 1 -> warmup INVALID
        var wl = S1Workload(qids, tamper: d => d[("S1", 1)] = d[("S1", 1)] with { Cardinality = 99 });
        var exec = new ServingTimingRunner(candidate, wl, "S1", 1).Execute();

        Assert.Equal(ServingStatuses.Invalid, exec.Correctness);
        Assert.Empty(exec.Samples);
        Assert.Null(exec.TimedPassWallSeconds);
        Assert.Equal(3, candidate.ConceptLog.Count); // warmup only
    }

    [Fact]
    public void WarmupError_BlocksTimed()
    {
        long[] qids = { 100, 200 };
        var candidate = new FakeServingCandidate { ThrowOnFirstCallQid = qids[0] };
        candidate.Concepts[qids[0]] = new ConceptHit(true, true, false);
        candidate.Concepts[qids[1]] = new ConceptHit(true, true, false);
        var wl = S1Workload(qids);
        var exec = new ServingTimingRunner(candidate, wl, "S1", 1).Execute();
        Assert.Equal(ServingStatuses.Error, exec.Correctness);
        Assert.Empty(exec.Samples);
        Assert.Single(candidate.ConceptLog); // warmup aborted at first probe
    }

    [Fact]
    public void TimedInvalid_Continues_TimedError_Stops()
    {
        // timed invalid continues
        long[] qids = { 100, 200, 300 };
        var (c1, r1) = RunSetup(qids, c => c.WrongOnSecondCallQid = 200);
        var e1 = r1.Execute();
        Assert.Equal(3, e1.Samples.Count);
        Assert.Equal(new[] { ServingStatuses.Valid, ServingStatuses.Invalid, ServingStatuses.Valid },
            e1.Samples.Select(s => s.CorrectnessStatus));
        Assert.Equal(ServingStatuses.Invalid, e1.Correctness);
        Assert.Equal(6, c1.ConceptLog.Count); // third probe still executed in timed pass

        // timed error stops the pass
        var (c2, r2) = RunSetup(qids, c => c.ThrowOnSecondCallQid = 200);
        var e2 = r2.Execute();
        Assert.Equal(2, e2.Samples.Count);
        Assert.Equal(ServingStatuses.Error, e2.Samples[1].CorrectnessStatus);
        Assert.Equal(ServingStatuses.Error, e2.Correctness);
        Assert.Equal(5, c2.ConceptLog.Count); // warmup 3 + timed 100,200(throws) -> 300 never timed
    }

    [Fact]
    public void S1Tail_RunsAfterTimed_NoSamples_AndAffectsCorrectness()
    {
        // valid tail
        long[] tails = { 900 };
        var (_, r1) = RunSetup(new long[] { 100, 200 }, tails: tails);
        var e1 = r1.Execute();
        Assert.Equal(ServingStatuses.Valid, e1.Correctness);
        Assert.Equal(2, e1.Samples.Count);
        Assert.DoesNotContain(e1.Samples, s => s.Sequence >= 500);

        // tampered tail expected digest -> measured valid but overall INVALID
        var c2 = new FakeServingCandidate();
        c2.Concepts[100] = new ConceptHit(true, true, false);
        c2.Concepts[200] = new ConceptHit(true, true, false);
        c2.Concepts[900] = new ConceptHit(true, true, false);
        var wl2 = S1Workload(new long[] { 100, 200 }, tails,
            tamper: d => d[("S1", 500)] = d[("S1", 500)] with { Digest = "00".PadRight(64, '0') });
        var e2 = new ServingTimingRunner(c2, wl2, "S1", 1).Execute();
        Assert.Equal(ServingStatuses.Invalid, e2.Correctness);
        Assert.Equal(2, e2.Samples.Count); // measured samples only; tail produced none
    }

    [Fact]
    public void Artifact_Deterministic_CreateNew_ZeroRecord_WarmupBlocked()
    {
        string path = Path.Combine(Path.GetTempPath(), "serving-samples-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var samples = new List<ServingTimedSample>
            {
                new("S1", 1, "Hit", 0.5, "VALID", 1, "abc"),
                new("S1", 2, "Hit", 6.0, "VALID", 1, "abc"),
            };
            ServingSampleArtifact.WriteCreateNew(path, samples);
            byte[] raw = File.ReadAllBytes(path);
            Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "writer must not emit a BOM");
            string text = Encoding.UTF8.GetString(raw);
            Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("\"correctness_status\":\"VALID\"", text);
            Assert.ThrowsAny<IOException>(() => ServingSampleArtifact.WriteCreateNew(path, samples)); // refuse overwrite

            string empty = path + ".empty";
            ServingSampleArtifact.WriteCreateNew(empty, Array.Empty<ServingTimedSample>());
            Assert.Equal(0, new FileInfo(empty).Length);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".empty"); } catch { }
        }
    }
}

public class ServingTimingBoundaryTests
{
    private sealed class WatchList<T> : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _inner;
        private int _enumerationCount;
        private const int AllowedEnumerations = 2;

        public WatchList(IReadOnlyList<T> inner) => _inner = inner;
        public T this[int index] => _inner[index];
        public int Count => _inner.Count;

        public IEnumerator<T> GetEnumerator()
        {
            _enumerationCount++;
            // A reintroduced timed .ToList()/enumeration would be the third
            // enumeration (warmup canonical + warmup-copy + timed) and throws,
            // making the run ERROR; the correct no-copy path stays VALID.
            if (_enumerationCount > AllowedEnumerations) throw new InvalidOperationException("unexpected extra enumeration");
            return _inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class S2Candidate : IStorageCandidate
    {
        public List<LexicalHit> Hits { get; } = new();
        public void Open() { }
        public void Dispose() { }
        public ConceptHit GetConcept(long qid) => new(false, false, false);
        public IReadOnlyList<LexicalHit> LookupLexical(string lang, string value) => new WatchList<LexicalHit>(Hits);
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long qid) => Array.Empty<LexicalRow>();
        public IReadOnlyList<long> GetInstanceOf(long subjectQid) => Array.Empty<long>();
        public IReadOnlyList<long> GetSubclassOf(long subjectQid) => Array.Empty<long>();
    }

    [Fact]
    public void S2TimedRegion_ContainsNoEnumeration_CanonicalizesAfterTimer()
    {
        var candidate = new S2Candidate { Hits = { new LexicalHit(7, "label") } };
        var probe = new ServingProbe("S2", 1, "Hit", true, null, "en", "alpha");
        var expected = new ServingWorkload
        {
            Probes = new[] { probe },
            Expected = new Dictionary<(string, long), ServingExpected>
            {
                [("S2", 1L)] = new("S2", 1, true, 1,
                    WorkloadOracle.LexMembersDigest(new List<(long, string)> { (7, "label") })),
            },
        };
        var script = new ScriptedTimer(new double[] { 1.0 });
        var exec = new ServingTimingRunner(candidate, expected, "S2", 1, () => script).Execute();

        // If any timed .ToList()/enumeration had been reintroduced, the watch list
        // would have been enumerated a third time and the run would be ERROR.
        Assert.Equal(ServingStatuses.Valid, exec.Correctness);
        Assert.Single(exec.Samples);
        Assert.Equal(1.0, exec.Samples[0].WallSeconds);
    }
}

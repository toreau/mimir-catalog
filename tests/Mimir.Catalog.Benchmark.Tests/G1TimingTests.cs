using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class G1TimingTests
{
    private sealed class ScriptedGraphCandidate : IStorageCandidate
    {
        private readonly Dictionary<long, int> _calls = new();
        public Func<long, int, IReadOnlyList<long>> SubclassOf { get; init; } = (_, _) => Array.Empty<long>();
        public Action<long>? OnSubclassOf { get; init; }
        public bool ThrowOnOpen { get; init; }
        public void Open() { if (ThrowOnOpen) throw new InvalidOperationException("open boom"); }
        public void Dispose() { }
        public ConceptHit GetConcept(long qid) => new(true, true, false);
        public IReadOnlyList<LexicalHit> LookupLexical(string lang, string value) => Array.Empty<LexicalHit>();
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long qid) => Array.Empty<LexicalRow>();
        public IReadOnlyList<long> GetInstanceOf(long qid) => Array.Empty<long>();
        public IReadOnlyList<long> GetSubclassOf(long qid)
        {
            _calls.TryGetValue(qid, out int n);
            _calls[qid] = n + 1;
            OnSubclassOf?.Invoke(qid);
            return SubclassOf(qid, n + 1);
        }
    }

    private sealed class LoggingTimer : ITimer
    {
        private readonly List<string> _log;
        private bool _running;
        public LoggingTimer(List<string> log) => _log = log;
        public void Start() { _running = true; _log.Add("start"); }
        public double StopSeconds()
        {
            if (!_running) throw new InvalidOperationException("not started");
            _running = false;
            _log.Add("stop");
            return 1.0;
        }
    }

    private static string EmptyDigest()
    {
        // Traversal of an empty-parent start: discovered empty, visited = {start}.
        return WorkloadOracle.G1Digest(Array.Empty<long>(), 1);
    }

    private static GraphWorkload G1Workload(int count)
    {
        var probes = new List<GraphProbe>();
        var expected = new Dictionary<(string, long), GraphExpected>();
        for (int seq = 0; seq < count; seq++)
        {
            probes.Add(new GraphProbe("G1", seq, seq % 2 == 0 ? "Degree1" : "Degree2Plus", true, 1000 + seq));
            expected[("G1", seq)] = new GraphExpected("G1", seq, true, 0, 1, EmptyDigest());
        }
        return new GraphWorkload { Probes = probes, Expected = expected };
    }

    [Fact]
    public void Full500_WarmupThenTimed_AllValid_InOrder()
    {
        var candidate = new ScriptedGraphCandidate();
        var result = new G1TimingRunner(candidate, G1Workload(500), repetition: 1).Execute();
        Assert.Equal(ServingStatuses.Valid, result.Correctness);
        Assert.Equal(500, result.Samples.Count);
        Assert.Equal(Enumerable.Range(0, 500).Select(i => (long)i), result.Samples.Select(s => s.Sequence));
        Assert.All(result.Samples, s =>
        {
            Assert.Equal(ServingStatuses.Valid, s.CorrectnessStatus);
            Assert.Equal(0, s.ActualCardinality);
            Assert.Equal(1, s.ActualVisited);
            Assert.Equal(EmptyDigest(), s.ActualDigest);
        });
        Assert.NotNull(result.TimedPassWallSeconds);
        Assert.Null(result.ErrorCategory);
    }

    [Fact]
    public void CompleteUntimedWarmupOccursBeforeFirstTimedStart()
    {
        var order = new List<long>();
        var candidate = new ScriptedGraphCandidate { OnSubclassOf = order.Add };
        new G1TimingRunner(candidate, G1Workload(50), repetition: 1).Execute();
        Assert.Equal(100, order.Count); // one warmup + one timed call per start
        var firstHalf = order.Take(50).ToList();
        var secondHalf = order.Skip(50).ToList();
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (long)(1000 + i)), firstHalf);
        Assert.Equal(firstHalf, secondHalf); // warmup completes before the timed pass begins
    }

    [Fact]
    public void WarmupInvalid_ZeroSamples_NoTimedPass_ContinuesAll()
    {
        long target = 1000;
        var order = new List<long>();
        var candidate = new ScriptedGraphCandidate
        {
            OnSubclassOf = order.Add,
            SubclassOf = (qid, call) => qid == target && call == 1 ? new[] { target + 1_000_000 } : Array.Empty<long>(),
        };
        var result = new G1TimingRunner(candidate, G1Workload(10), repetition: 1).Execute();
        Assert.Equal(ServingStatuses.Invalid, result.Correctness);
        Assert.Empty(result.Samples);
        Assert.Null(result.TimedPassWallSeconds);
        Assert.Null(result.ErrorCategory);
        Assert.True(Enumerable.Range(1000, 10).All(q => order.Contains(q))); // full warmup covered every start
    }

    [Fact]
    public void WarmupError_ZeroSamples_CategoryWarmup()
    {
        long target = 1000;
        var candidate = new ScriptedGraphCandidate
        {
            SubclassOf = (qid, call) => qid == target && call == 1 ? throw new InvalidOperationException("warmup boom") : Array.Empty<long>(),
        };
        var result = new G1TimingRunner(candidate, G1Workload(10), repetition: 1).Execute();
        Assert.Equal(ServingStatuses.Error, result.Correctness);
        Assert.Empty(result.Samples);
        Assert.Equal("warmup", result.ErrorCategory);
        Assert.Equal("warmup boom", result.ErrorMessage);
    }

    [Fact]
    public void TimedInvalid_ContinuesToLaterStarts()
    {
        long target = 1004; // seq 4, child outside the start range to avoid cross-talk
        var candidate = new ScriptedGraphCandidate
        {
            SubclassOf = (qid, call) => qid == target && call == 2 ? new[] { target + 1_000_000 } : Array.Empty<long>(),
        };
        var result = new G1TimingRunner(candidate, G1Workload(25), repetition: 1).Execute();
        Assert.Equal(ServingStatuses.Invalid, result.Correctness);
        Assert.Equal(25, result.Samples.Count);
        Assert.All(result.Samples.Take(4), s => Assert.Equal(ServingStatuses.Valid, s.CorrectnessStatus));
        Assert.Equal(ServingStatuses.Invalid, result.Samples[4].CorrectnessStatus);
        Assert.Equal(1, result.Samples[4].ActualCardinality); // differing actual fact retained
        Assert.All(result.Samples.Skip(5), s => Assert.Equal(ServingStatuses.Valid, s.CorrectnessStatus));
    }

    [Fact]
    public void TimedError_StopsImmediately_StrictPrefixEndsInError()
    {
        long target = 1003; // seq 3
        var order = new List<long>();
        var candidate = new ScriptedGraphCandidate
        {
            OnSubclassOf = order.Add,
            SubclassOf = (qid, call) =>
                qid == target && call == 2 ? throw new InvalidOperationException("timed boom") : Array.Empty<long>(),
        };
        var result = new G1TimingRunner(candidate, G1Workload(10), repetition: 1).Execute();
        Assert.Equal(ServingStatuses.Error, result.Correctness);
        Assert.Equal("timed-start", result.ErrorCategory);
        Assert.Equal("timed boom", result.ErrorMessage);
        Assert.Equal(4, result.Samples.Count); // seq 0..3 inclusive prefix
        Assert.All(result.Samples.Take(3), s => Assert.Equal(ServingStatuses.Valid, s.CorrectnessStatus));
        Assert.Equal(ServingStatuses.Error, result.Samples[3].CorrectnessStatus);
        Assert.Equal("timed boom", result.Samples[3].Error);
        // Later starts never executed their timed traversal (only warmup).
        Assert.Equal(10 + 4, order.Count);
    }

    [Fact]
    public void WallAtOrAbove30_RemainsChildValid_NeverTimeout()
    {
        var candidate = new ScriptedGraphCandidate();
        var shared = new ScriptedTimer(new[] { 30.0, 31.5, 29.9, 35.0, 42.0 });
        var result = new G1TimingRunner(candidate, G1Workload(5), repetition: 1, () => shared).Execute();
        Assert.Equal(ServingStatuses.Valid, result.Correctness);
        Assert.Equal(new[] { 30.0, 31.5, 29.9, 35.0, 42.0 }, result.Samples.Select(s => s.WallSeconds));
        Assert.DoesNotContain(result.Samples, s => s.CorrectnessStatus == "TIMEOUT");
    }

    [Fact]
    public void TimerBoundary_CandidateCallsInsideStartStopWindow()
    {
        var log = new List<string>();
        var candidate = new ScriptedGraphCandidate { OnSubclassOf = qid => log.Add($"candidate:{qid}") };
        var runner = new G1TimingRunner(candidate, G1Workload(2), repetition: 1,
            () => new LoggingTimer(log));
        runner.Execute();
        // Warmup first (candidate only), then per timed probe start..candidate..stop.
        var warmup = log.Take(2).ToList();
        Assert.All(warmup, m => Assert.StartsWith("candidate:", m));
        var timed = log.Skip(2).ToList();
        Assert.Equal(6, timed.Count);
        Assert.Equal(new[] { "start", $"candidate:{1000}", "stop", "start", $"candidate:{1001}", "stop" }, timed);
    }
}

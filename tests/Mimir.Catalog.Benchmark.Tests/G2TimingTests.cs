using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class G2TimingTests
{
    private sealed class ScriptedCandidate : IStorageCandidate
    {
        private readonly Dictionary<long, int> _instanceCalls = new();
        private readonly Dictionary<long, int> _subclassCalls = new();
        public Func<long, int, IReadOnlyList<long>> InstanceOf { get; init; } = (_, _) => Array.Empty<long>();
        public Func<long, int, IReadOnlyList<long>> SubclassOf { get; init; } = (_, _) => Array.Empty<long>();
        public Action<string>? OnCall { get; init; }
        public void Open() { }
        public void Dispose() { }
        public ConceptHit GetConcept(long qid) => new(true, true, false);
        public IReadOnlyList<LexicalHit> LookupLexical(string lang, string value) => Array.Empty<LexicalHit>();
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long qid) => Array.Empty<LexicalRow>();
        public IReadOnlyList<long> GetInstanceOf(long qid)
        {
            _instanceCalls.TryGetValue(qid, out int n);
            _instanceCalls[qid] = n + 1;
            OnCall?.Invoke($"instance:{qid}");
            return InstanceOf(qid, n + 1);
        }
        public IReadOnlyList<long> GetSubclassOf(long qid)
        {
            _subclassCalls.TryGetValue(qid, out int n);
            _subclassCalls[qid] = n + 1;
            OnCall?.Invoke($"subclass:{qid}");
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

    private static G2Workload Workload(int count)
    {
        var concepts = new List<G2Concept>();
        var perInput = new List<G2PerInputExpected>();
        for (int i = 0; i < count; i++)
        {
            concepts.Add(new G2Concept(1000 + i, i % 2 == 0 ? "P31Degree1" : "P31Degree2Plus"));
            perInput.Add(new G2PerInputExpected(i, 1000 + i, i % 2 == 0 ? "P31Degree1" : "P31Degree2Plus", 0,
                WorkloadOracle.StructuralSetDigest(Array.Empty<long>())));
        }
        var rows = concepts.Select(c => (c.Qid, Array.Empty<long>())).ToList();
        var batch = new G2BatchExpected(count, WorkloadOracle.G2BatchDigest(rows));
        return new G2Workload { Concepts = concepts, PerInput = perInput, Batch = batch };
    }

    [Fact]
    public void WarmupValid_ExactlyOneTimedBatch_ValidWithFacts()
    {
        var candidate = new ScriptedCandidate();
        var result = new G2TimingRunner(candidate, Workload(200), repetition: 1).Execute();
        Assert.Equal(ServingStatuses.Valid, result.Correctness);
        Assert.Equal(200, result.PerInputResults.Count);
        Assert.NotNull(result.BatchResult);
        Assert.Equal(ServingStatuses.Valid, result.BatchResult!.CorrectnessStatus);
        Assert.Equal(200, result.BatchResult.ActualCardinality);
        Assert.NotNull(result.BatchResult.ActualDigest);
        Assert.Null(result.ErrorCategory);
    }

    [Fact]
    public void WarmupCompletesBeforeTimerStarts_AllTimedCallsInsideStartStop_NoCallsAfterStop()
    {
        var log = new List<string>();
        var candidate = new ScriptedCandidate { OnCall = log.Add };
        var runner = new G2TimingRunner(candidate, Workload(5), repetition: 1, () => new LoggingTimer(log));
        runner.Execute();
        // 5 warmup instance calls, then start, then 5 timed instance calls, then stop.
        Assert.Equal(12, log.Count);
        Assert.All(log.Take(5), m => Assert.StartsWith("instance:", m));
        Assert.Equal("start", log[5]);
        Assert.All(log.Skip(6).Take(5), m => Assert.StartsWith("instance:", m));
        Assert.Equal("stop", log[^1]); // Classify performs no candidate calls after Stop
    }

    [Fact]
    public void WarmupInvalid_BlocksTimedBatch_ZeroRecords()
    {
        // Empty warmup would be VALID; first-call target parent flips a per-input.
        long target = 1000;
        var candidate = new ScriptedCandidate
        {
            InstanceOf = (qid, call) => qid == target && call == 1 ? new[] { target + 1_000_000 } : Array.Empty<long>(),
        };
        var result = new G2TimingRunner(candidate, Workload(5), repetition: 1).Execute();
        Assert.Equal(ServingStatuses.Invalid, result.Correctness);
        Assert.Empty(result.PerInputResults);
        Assert.Null(result.BatchResult);
        Assert.Null(result.ErrorCategory);
    }

    [Fact]
    public void WarmupError_BlocksTimedBatch_CategoryWarmup()
    {
        long target = 1000;
        var candidate = new ScriptedCandidate
        {
            InstanceOf = (qid, call) => qid == target ? throw new InvalidOperationException("instance boom") : Array.Empty<long>(),
        };
        var result = new G2TimingRunner(candidate, Workload(5), repetition: 1).Execute();
        Assert.Equal(ServingStatuses.Error, result.Correctness);
        Assert.Equal("warmup", result.ErrorCategory);
        Assert.NotNull(result.ErrorMessage);
        Assert.Empty(result.PerInputResults);
        Assert.Null(result.BatchResult);
    }

    [Fact]
    public void TimedInvalid_RetainsActualBatchFacts()
    {
        // Warmup empty (call 1) valid; timed instance (call 2) yields a target.
        long target = 1001;
        var candidate = new ScriptedCandidate
        {
            InstanceOf = (qid, call) => qid == target && call == 2 ? new[] { target + 1_000_000 } : Array.Empty<long>(),
        };
        var result = new G2TimingRunner(candidate, Workload(5), repetition: 1,
            () => new ScriptedTimer(new[] { 1.0 })).Execute();
        Assert.Equal(ServingStatuses.Invalid, result.Correctness);
        Assert.Equal(5, result.PerInputResults.Count);
        Assert.NotNull(result.BatchResult);
        Assert.Equal(ServingStatuses.Invalid, result.BatchResult!.CorrectnessStatus);
        Assert.Equal(5, result.BatchResult.ActualCardinality); // positional batch rows retained
        Assert.NotNull(result.BatchResult.ActualDigest);
        Assert.Equal(1.0, result.BatchResult.WallSeconds);
        Assert.Equal(ServingStatuses.Invalid, result.PerInputResults[1].CorrectnessStatus);
        Assert.NotNull(result.PerInputResults[1].ActualDigest);
    }

    [Fact]
    public void TimedPerInputError_ContinuesLaterConcepts_BatchErrorNullFacts()
    {
        long target = 1002;
        var candidate = new ScriptedCandidate
        {
            InstanceOf = (qid, call) => qid == target && call == 2 ? throw new InvalidOperationException("timed boom") : Array.Empty<long>(),
        };
        var result = new G2TimingRunner(candidate, Workload(5), repetition: 1,
            () => new ScriptedTimer(new[] { 1.0 })).Execute();
        Assert.Equal(ServingStatuses.Error, result.Correctness);
        Assert.Equal("timed-batch", result.ErrorCategory);
        Assert.Equal(5, result.PerInputResults.Count); // later concepts still processed
        Assert.Equal(ServingStatuses.Error, result.PerInputResults[2].CorrectnessStatus);
        Assert.Equal("timed boom", result.PerInputResults[2].Error);
        Assert.Equal(ServingStatuses.Valid, result.PerInputResults[4].CorrectnessStatus);
        Assert.NotNull(result.BatchResult);
        Assert.Equal(ServingStatuses.Error, result.BatchResult!.CorrectnessStatus);
        Assert.Null(result.BatchResult.ActualCardinality);
        Assert.Null(result.BatchResult.ActualDigest);
        Assert.NotNull(result.BatchResult.Error);
    }

    [Theory]
    [InlineData(120.0)]
    [InlineData(130.0)]
    public void WallAtOrAbove120_RemainsChildValid_NeverTimeout(double wall)
    {
        var candidate = new ScriptedCandidate();
        var result = new G2TimingRunner(candidate, Workload(3), repetition: 1,
            () => new ScriptedTimer(new[] { wall })).Execute();
        Assert.Equal(ServingStatuses.Valid, result.Correctness);
        Assert.Equal(wall, result.BatchResult!.WallSeconds);
        Assert.DoesNotContain("TIMEOUT", result.BatchResult.CorrectnessStatus);
    }
}

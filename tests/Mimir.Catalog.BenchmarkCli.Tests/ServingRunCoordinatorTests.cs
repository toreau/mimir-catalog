using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class ServingRunCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-src-" + Guid.NewGuid().ToString("N"));

    public ServingRunCoordinatorTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private RunIdentity Identity() => new()
    {
        EvidenceSchemaVersion = EvidenceSchema.Version,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = "run-1",
    };

    private static readonly string[] Ops = { "S1", "S2", "S3", "S4", "S5" };

    private ServingWorkload Workload()
    {
        var probes = new List<ServingProbe>();
        var expected = new Dictionary<(string, long), ServingExpected>();
        foreach (var op in Ops)
        {
            probes.Add(new ServingProbe(op, 1, "Hit", true, 100 + (op[1] - '0'), null, null));
            expected[(op, 1)] = new ServingExpected(op, 1, true, 1, "d");
        }
        return new ServingWorkload { Probes = probes, Expected = expected };
    }

    private static ChildResultEnvelope Envelope(string op, int rep, LogicalStatus status, string correctness)
        => new()
        {
            ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
            CandidateId = CandidateAIdentity.CandidateId,
            CandidateConfigId = CandidateAIdentity.CandidateConfigId,
            WorkloadId = CandidateAIdentity.WorkloadId,
            CorpusId = CandidateAIdentity.CorpusId,
            WorkloadClass = WorkloadClass.Serving,
            Operation = op,
            Repetition = rep,
            Status = status,
            CorrectnessStatus = correctness,
        };

    private static ServingChildEvidenceResult Child(
        string op, int rep,
        LogicalStatus status = LogicalStatus.Valid,
        string correctness = "VALID",
        ProcessOutcome outcome = ProcessOutcome.CompletedProtocolResult,
        ResourceMeasurementStatus resource = ResourceMeasurementStatus.Valid,
        bool evidenceValid = true,
        bool stable = true,
        bool measuredComplete = true,
        IReadOnlyList<ServingParentSample>? samples = null)
        => new()
        {
            Operation = op,
            Repetition = rep,
            ProcessOutcome = outcome,
            ResourceStatus = resource,
            Envelope = Envelope(op, rep, status, correctness),
            ParentSamples = samples ?? new[] { new ServingParentSample(op, 1, "Hit", 0.5, TimedResultStatus.Valid, "VALID") },
            MeasuredSequenceComplete = measuredComplete,
            EvidenceValid = evidenceValid,
            EvidenceProblems = Array.Empty<string>(),
            WatchdogSeconds = 3600,
            RegisteredStableArtifacts = stable,
        };

    private EvidenceStagingSession NewSession()
    {
        string runs = Path.Combine(_root, "runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runs);
        return EvidenceStagingSession.Create(runs, Identity());
    }

    private static async Task<ServingRunCoordinatorResult> RunCoordinator(
        EvidenceStagingSession session, ServingWorkload workload,
        Func<string, int, ServingWorkload, Task<ServingChildEvidenceResult>>? oneChild = null,
        Action<string, int, TimeSpan>? probe = null)
    {
        return await ServingRunCoordinator.RunAsync(
            session, "/fixture/candidate.db", "/fixture/workload", workload,
            reqPath => ProcessInvocation.BenchmarkChild("/fixture/child.exe", reqPath),
            TimeSpan.FromSeconds(3600), oneChild, probe).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullSuccess_ExactOrder_15Calls_SameWatchdog_Complete()
    {
        using var s = NewSession();
        var order = new List<(string, int)>();
        var watchdogs = new List<double>();
        var result = await RunCoordinator(s, Workload(),
            oneChild: (op, rep, _) => Task.FromResult(Child(op, rep)),
            probe: (op, rep, wd) => { order.Add((op, rep)); watchdogs.Add(wd.TotalSeconds); });
        var expected = Ops.SelectMany(op => Enumerable.Range(1, 3).Select(r => (op, r))).ToList();
        Assert.Equal(expected, order);
        Assert.Equal(15, result.AttemptedExecutionCount);
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.False(result.Halted);
        Assert.All(watchdogs, wd => Assert.Equal(3600, wd));
        Assert.Equal(15, result.RepetitionSummaries.Count);
        foreach (var sm in result.RepetitionSummaries)
            Assert.True(sm.Status == ServingSummaryStatus.Valid,
                $"{sm.Operation}-{sm.Repetition}: {string.Join("|", sm.Reasons)}");
    }

    [Theory]
    [InlineData(LogicalStatus.Invalid, "INVALID")]
    [InlineData(LogicalStatus.Error, "ERROR")]
    public async Task BenchmarkInvalidOrError_DoesNotStop(LogicalStatus status, string correctness)
    {
        using var s = NewSession();
        int calls = 0;
        var result = await RunCoordinator(s, Workload(),
            oneChild: (op, rep, _) => { calls++; return Task.FromResult(Child(op, rep, status, correctness, samples: Array.Empty<ServingParentSample>())); });
        Assert.Equal(15, calls);
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.All(result.RepetitionSummaries, sm => Assert.Equal(ServingSummaryStatus.Incomplete, sm.Status));
    }

    [Fact]
    public async Task PointTimeout_DoesNotStop_SummaryIncomplete()
    {
        using var s = NewSession();
        var result = await RunCoordinator(s, Workload(), oneChild: (op, rep, _) => Task.FromResult(
            Child(op, rep, samples: new[] { new ServingParentSample(op, 1, "Hit", 5.0, TimedResultStatus.Timeout, "VALID") })));
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.All(result.RepetitionSummaries, sm => Assert.Contains(ServingIncompleteReason.TimeoutSample, sm.Reasons));
    }

    [Fact]
    public async Task ResourceErrorOrUnavailable_DoesNotStop()
    {
        using var s = NewSession();
        bool useError = true;
        var result = await RunCoordinator(s, Workload(), oneChild: (op, rep, _) => Task.FromResult(
            Child(op, rep, resource: useError ? ResourceMeasurementStatus.Error : ResourceMeasurementStatus.Unavailable)));
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.Equal("Error", result.Executions[0].ResourceStatus);
    }

    [Theory]
    [InlineData(false, true, "evidence invalid")]
    [InlineData(true, false, "stable artifact capture failed")]
    public async Task EvidenceOrCaptureFailure_HaltsImmediately(bool evidence, bool stable, string expectedReason)
    {
        using var s = NewSession();
        var order = new List<(string, int)>();
        var result = await RunCoordinator(s, Workload(),
            oneChild: (op, rep, _) =>
            {
                order.Add((op, rep));
                return Task.FromResult(Child(op, rep, evidenceValid: evidence, stable: stable));
            });
        Assert.Equal(1, order.Count);
        Assert.Equal(1, result.AttemptedExecutionCount);
        Assert.True(result.Halted);
        Assert.False(result.CoordinatorComplete);
        Assert.False(result.EvidenceValid);
        Assert.Equal("S1", result.HaltAfterOperation);
        Assert.Equal(1, result.HaltAfterRepetition);
        Assert.Equal(expectedReason, result.HaltReason);
    }

    [Fact]
    public async Task Halt_ProducesExplicitNotAttemptedRecords()
    {
        using var s = NewSession();
        var result = await RunCoordinator(s, Workload(),
            oneChild: (op, rep, _) => Task.FromResult(Child(op, rep, evidenceValid: op != "S1" || rep != 1 ? true : false)));
        var notAttempted = result.RepetitionSummaries.Where(sm => sm.Reasons.Contains(ServingIncompleteReason.NotAttemptedDueToHalt)).ToList();
        Assert.Equal(14, notAttempted.Count);
        var summary = result.RepetitionSummaries.Single(sm => sm.Operation == "S1" && sm.Repetition == 3);
        Assert.Contains(ServingIncompleteReason.NotAttemptedDueToHalt, summary.Reasons);
        Assert.Equal(ServingSummaryStatus.Incomplete, summary.Status);
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public async Task S1TailInvalidEnvelope_MakesRepetitionIncomplete()
    {
        using var s = NewSession();
        // Complete measured all-valid samples but envelope INVALID (S1 Tail form).
        var result = await RunCoordinator(s, Workload(), oneChild: (op, rep, _) =>
            op == "S1"
                ? Task.FromResult(Child(op, rep, LogicalStatus.Invalid, "INVALID",
                    samples: new[] { new ServingParentSample(op, 1, "Hit", 0.5, TimedResultStatus.Valid, "VALID") }))
                : Task.FromResult(Child(op, rep)));
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        var s1 = result.RepetitionSummaries.Where(sm => sm.Operation == "S1").ToList();
        Assert.Equal(3, s1.Count);
        Assert.All(s1, sm =>
        {
            Assert.Equal(ServingSummaryStatus.Incomplete, sm.Status);
            Assert.Contains(ServingIncompleteReason.EnvelopeNotValid, sm.Reasons);
            Assert.Null(sm.Metrics);
        });
    }

    [Fact]
    public async Task CoordinatorWriteCollision_EvidenceInvalidFalse()
    {
        using var s = NewSession();
        string physical = Path.Combine(s.StagingPath, "serving", "coordinator.json");
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        File.WriteAllText(physical, "occupied");
        var result = await RunCoordinator(s, Workload(), oneChild: (op, rep, _) => Task.FromResult(Child(op, rep)));
        Assert.True(result.CoordinatorComplete);
        Assert.False(result.EvidenceValid);
        Assert.Equal(15, result.AttemptedExecutionCount);
    }

    [Fact]
    public async Task DeterministicEvidenceAndNoPublication()
    {
        using var s = NewSession();
        var result = await RunCoordinator(s, Workload(), oneChild: (op, rep, _) => Task.FromResult(Child(op, rep)));
        string coordinator = File.ReadAllText(Path.Combine(s.StagingPath, "serving", "coordinator.json"));
        Assert.Contains("\"planned_execution_count\":15", coordinator);
        Assert.Contains("\"watchdog_seconds\":3600", coordinator);
        string summaries = File.ReadAllText(Path.Combine(s.StagingPath, "serving", "repetition-summaries.json"));
        Assert.Contains("\"operation\":\"S1\"", summaries);
        Assert.Equal(15, result.RepetitionSummaries.Count);
        // Ordering: S1..S5 -> stratum ordinal -> rep 1..3.
        var expected = Ops.SelectMany(op => new[] { "Hit" }.SelectMany(stratum =>
            Enumerable.Range(1, 3).Select(r => (op, r)))).ToList();
        var actual = result.RepetitionSummaries.Select(sm => (sm.Operation, sm.Repetition)).ToList();
        Assert.Equal(expected, actual);
    }
}

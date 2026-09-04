using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class G1RunCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-g1c-" + Guid.NewGuid().ToString("N"));

    public G1RunCoordinatorTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static RunIdentity Identity() => new()
    {
        EvidenceSchemaVersion = EvidenceSchema.Version,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = "run-1",
    };

    private static GraphWorkload Workload()
    {
        var probes = new List<GraphProbe>
        {
            new("G1", 0, "Degree1", true, 1000),
            new("G1", 1, "Degree2Plus", true, 1001),
        };
        return new GraphWorkload
        {
            Probes = probes,
            Expected = new Dictionary<(string, long), GraphExpected>
            {
                [("G1", 0L)] = new("G1", 0, true, 0, 1, "d1"),
                [("G1", 1L)] = new("G1", 1, true, 0, 1, "d2"),
            },
        };
    }

    private static G1ParentSample Sample(long seq, string stratum, TimedResultStatus status = TimedResultStatus.Valid, double wall = 0.5)
        => new("G1", seq, stratum, wall, status, status == TimedResultStatus.Invalid ? "INVALID" : status == TimedResultStatus.Error ? "ERROR" : "VALID");

    private static IReadOnlyList<G1ParentSample> ValidSamples()
        => new[] { Sample(0, "Degree1"), Sample(1, "Degree2Plus") };

    private static ChildResultEnvelope Envelope(string correctness, LogicalStatus status)
        => new()
        {
            ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
            CandidateId = CandidateAIdentity.CandidateId,
            CandidateConfigId = CandidateAIdentity.CandidateConfigId,
            WorkloadId = CandidateAIdentity.WorkloadId,
            CorpusId = CandidateAIdentity.CorpusId,
            WorkloadClass = WorkloadClass.G1,
            Operation = "G1",
            Repetition = 1,
            Status = status,
            CorrectnessStatus = correctness,
        };

    private static G1ChildEvidenceResult Child(
        int rep,
        string correctness = "VALID",
        LogicalStatus status = LogicalStatus.Valid,
        bool evidenceValid = true,
        bool stable = true,
        ResourceMeasurementStatus resource = ResourceMeasurementStatus.Valid,
        ProcessOutcome outcome = ProcessOutcome.CompletedProtocolResult,
        IReadOnlyList<G1ParentSample>? samples = null)
        => new()
        {
            Operation = "G1",
            Repetition = rep,
            ProcessOutcome = outcome,
            ResourceStatus = resource,
            Envelope = Envelope(correctness, status),
            ParentSamples = samples ?? ValidSamples(),
            MeasuredSequenceComplete = true,
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

    private static async Task<G1RunCoordinatorResult> RunCoordinator(
        EvidenceStagingSession session,
        Func<int, Task<G1ChildEvidenceResult>>? oneChild = null,
        Action<int, TimeSpan>? probe = null)
    {
        return await G1RunCoordinator.RunAsync(
            session, "/fixture/candidate.db", "/fixture/workload", Workload(),
            reqPath => ProcessInvocation.BenchmarkChild("/fixture/child.exe", reqPath),
            TimeSpan.FromSeconds(3600), oneChild, probe).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullSuccess_SerialOrder_SameWatchdog_AllValid()
    {
        using var s = NewSession();
        var order = new List<int>();
        var watchdogs = new List<double>();
        var result = await RunCoordinator(s,
            oneChild: rep => { order.Add(rep); return Task.FromResult(Child(rep)); },
            probe: (rep, wd) => watchdogs.Add(wd.TotalSeconds));
        Assert.Equal(new[] { 1, 2, 3 }, order);
        Assert.Equal(3, result.AttemptedExecutionCount);
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.True(result.CoordinatorArtifactWritten);
        Assert.True(result.RepetitionSummariesArtifactWritten);
        Assert.All(watchdogs, wd => Assert.Equal(3600, wd));
        Assert.Equal(6, result.RepetitionSummaries.Count);
        Assert.All(result.RepetitionSummaries, sm => Assert.Equal(G1SummaryStatus.Valid, sm.Status));
        Assert.Equal(0.5, result.RepetitionSummaries[0].Metrics!.MeanSeconds);
    }

    [Fact]
    public async Task BenchmarkInvalidOrErrorOrTimeout_DoesNotHalt()
    {
        using var s = NewSession();
        int calls = 0;
        var result = await RunCoordinator(s, oneChild: rep =>
        {
            calls++;
            return rep switch
            {
                1 => Task.FromResult(Child(rep, "INVALID", LogicalStatus.Invalid)),
                2 => Task.FromResult(Child(rep, "ERROR", LogicalStatus.Error, samples: new[] { Sample(0, "Degree1", TimedResultStatus.Error, 1.0), Sample(1, "Degree2Plus") })),
                _ => Task.FromResult(Child(rep, samples: new[] { Sample(0, "Degree1", TimedResultStatus.Timeout, 31.0), Sample(1, "Degree2Plus") })),
            };
        });
        Assert.Equal(3, calls);
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.True(result.EvidenceValid);
        var rep1 = result.RepetitionSummaries.Where(sm => sm.Repetition == 1).ToList();
        Assert.All(rep1, sm => Assert.Contains(G1IncompleteReason.EnvelopeNotValid, sm.Reasons));
        Assert.True(result.RepetitionSummaries.Any(sm => sm.Repetition == 3 && sm.Reasons.Contains(G1IncompleteReason.TimeoutSample)));
    }

    [Theory]
    [InlineData(false, true, "evidence invalid")]
    [InlineData(true, false, "stable artifact capture failed")]
    public async Task EvidenceOrCaptureFailure_HaltsImmediately(bool evidence, bool stable, string expectedReason)
    {
        using var s = NewSession();
        var order = new List<int>();
        var result = await RunCoordinator(s, oneChild: rep =>
        {
            order.Add(rep);
            return Task.FromResult(Child(rep, evidenceValid: evidence, stable: stable));
        });
        Assert.Equal(new[] { 1 }, order);
        Assert.True(result.Halted);
        Assert.Equal(1, result.HaltAfterRepetition);
        Assert.Equal(expectedReason, result.HaltReason);
        Assert.False(result.CoordinatorComplete);
        Assert.False(result.EvidenceValid);
        var notAttempted = result.RepetitionSummaries.Where(sm => sm.Reasons.Contains(G1IncompleteReason.NotAttemptedDueToHalt)).ToList();
        Assert.Equal(4, notAttempted.Count); // 2 strata × reps 2,3
    }

    [Fact]
    public async Task ResourceErrorOrUnavailable_DoesNotHalt()
    {
        using var s = NewSession();
        int calls = 0;
        var result = await RunCoordinator(s, oneChild: rep =>
        {
            calls++;
            var resource = rep == 1 ? ResourceMeasurementStatus.Error : ResourceMeasurementStatus.Unavailable;
            return Task.FromResult(Child(rep, resource: resource));
        });
        Assert.Equal(3, calls);
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.All(result.RepetitionSummaries, sm => Assert.Equal(G1SummaryStatus.Valid, sm.Status));
    }

    [Fact]
    public async Task CoordinatorArtifactCollision_EvidenceInvalid()
    {
        using var s = NewSession();
        string physical = Path.Combine(s.StagingPath, "graph", "g1", "coordinator.json");
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        File.WriteAllText(physical, "occupied");
        var result = await RunCoordinator(s, oneChild: rep => Task.FromResult(Child(rep)));
        Assert.True(result.CoordinatorComplete);
        Assert.False(result.CoordinatorArtifactWritten);
        Assert.True(result.RepetitionSummariesArtifactWritten);
        Assert.False(result.EvidenceValid);
        Assert.Equal("occupied", File.ReadAllText(physical));
    }

    [Fact]
    public async Task SummaryArtifactCollision_EvidenceInvalid_ChildrenRetained()
    {
        using var s = NewSession();
        string physical = Path.Combine(s.StagingPath, "graph", "g1", "repetition-summaries.json");
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        File.WriteAllText(physical, "occupied");
        var result = await RunCoordinator(s, oneChild: rep => Task.FromResult(Child(rep)));
        Assert.True(result.CoordinatorArtifactWritten);
        Assert.False(result.RepetitionSummariesArtifactWritten);
        Assert.False(result.EvidenceValid);
        Assert.Equal(3, result.AttemptedExecutionCount);
        Assert.Equal(6, result.RepetitionSummaries.Count);
    }

    [Fact]
    public async Task DeterministicEvidence_Ordering_Content()
    {
        using var s = NewSession();
        var result = await RunCoordinator(s, oneChild: rep => Task.FromResult(Child(rep)));
        string coordinator = File.ReadAllText(Path.Combine(s.StagingPath, "graph", "g1", "coordinator.json"));
        Assert.Contains("\"planned_execution_count\":3", coordinator);
        Assert.Contains("\"child_evidence_valid\":true", coordinator);
        Assert.Contains("\"watchdog_seconds\":3600", coordinator);
        string summaries = File.ReadAllText(Path.Combine(s.StagingPath, "graph", "g1", "repetition-summaries.json"));
        Assert.Contains("\"operation\":\"G1\"", summaries);
        // deterministic stratum ordinal then repetition ordering
        var expected = new[] { "Degree1", "Degree2Plus" }.SelectMany(st =>
            Enumerable.Range(1, 3).Select(rep => (st, rep))).ToList();
        var actual = result.RepetitionSummaries.Select(sm => (sm.Stratum, sm.Repetition)).ToList();
        Assert.Equal(expected, actual);
    }
}

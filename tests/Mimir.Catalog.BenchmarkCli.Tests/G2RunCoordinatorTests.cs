using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class G2RunCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-g2c-" + Guid.NewGuid().ToString("N"));

    public G2RunCoordinatorTests() => Directory.CreateDirectory(_root);
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

    private static G2Workload Workload(int count = 3)
    {
        var concepts = Enumerable.Range(0, count)
            .Select(i => new G2Concept(1000 + i, i % 2 == 0 ? "P31Degree1" : "P31Degree2Plus"))
            .ToList();
        var perInput = concepts.Select((c, i) => new G2PerInputExpected(i, c.Qid, c.SourceStratum, 0, "d")).ToList();
        return new G2Workload
        {
            Concepts = concepts,
            PerInput = perInput,
            Batch = new G2BatchExpected(count, "batch"),
        };
    }

    private static IReadOnlyList<G2ParentPerInput> PerInputs(int count)
        => Enumerable.Range(0, count)
            .Select(i => new G2ParentPerInput(i, 1000 + i, i % 2 == 0 ? "P31Degree1" : "P31Degree2Plus", ServingStatuses.Valid, 0, "d"))
            .ToList();

    private static ChildResultEnvelope Envelope(string correctness, LogicalStatus status)
        => new()
        {
            ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
            CandidateId = CandidateAIdentity.CandidateId,
            CandidateConfigId = CandidateAIdentity.CandidateConfigId,
            WorkloadId = CandidateAIdentity.WorkloadId,
            CorpusId = CandidateAIdentity.CorpusId,
            WorkloadClass = WorkloadClass.G2,
            Operation = "G2",
            Repetition = 1,
            Status = status,
            CorrectnessStatus = correctness,
        };

    private static G2ChildEvidenceResult Child(
        int rep,
        string correctness = "VALID",
        LogicalStatus status = LogicalStatus.Valid,
        bool evidenceValid = true,
        bool stable = true,
        bool timedComplete = true,
        ResourceMeasurementStatus resource = ResourceMeasurementStatus.Valid,
        ProcessOutcome outcome = ProcessOutcome.CompletedProtocolResult,
        G2ParentBatch? batch = null,
        int perInputCount = 3)
        => new()
        {
            Operation = "G2",
            Repetition = rep,
            ProcessOutcome = outcome,
            ResourceStatus = resource,
            Envelope = Envelope(correctness, status),
            PerInput = PerInputs(perInputCount),
            Batch = batch,
            TimedBatchComplete = timedComplete,
            EvidenceValid = evidenceValid,
            EvidenceProblems = Array.Empty<string>(),
            WatchdogSeconds = 3600,
            RegisteredStableArtifacts = stable,
        };

    private static G2ParentBatch Batch(TimedResultStatus status, double wall = 10.0)
        => new(wall, status, status switch
        {
            TimedResultStatus.Invalid => "INVALID",
            TimedResultStatus.Error => "ERROR",
            _ => "VALID",
        });

    private EvidenceStagingSession NewSession()
    {
        string runs = Path.Combine(_root, "runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runs);
        return EvidenceStagingSession.Create(runs, Identity());
    }

    private static async Task<G2RunCoordinatorResult> RunCoordinator(
        EvidenceStagingSession session,
        Func<int, Task<G2ChildEvidenceResult>>? oneChild = null,
        Action<int, TimeSpan>? probe = null)
    {
        return await G2RunCoordinator.RunAsync(
            session, "/fixture/candidate.db", "/fixture/workload", Workload(),
            reqPath => ProcessInvocation.BenchmarkChild("/fixture/child.exe", reqPath),
            TimeSpan.FromSeconds(3600), oneChild, probe).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullSuccess_SerialOrder_SameWatchdog_ThreeValidSummaries()
    {
        using var s = NewSession();
        var order = new List<int>();
        var watchdogs = new List<double>();
        var result = await RunCoordinator(s,
            oneChild: rep => { order.Add(rep); return Task.FromResult(Child(rep, batch: Batch(TimedResultStatus.Valid))); },
            probe: (rep, wd) => watchdogs.Add(wd.TotalSeconds));
        Assert.Equal(new[] { 1, 2, 3 }, order);
        Assert.Equal(3, result.AttemptedExecutionCount);
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.True(result.CoordinatorArtifactWritten);
        Assert.True(result.RepetitionSummariesArtifactWritten);
        Assert.All(watchdogs, wd => Assert.Equal(3600, wd));
        Assert.Equal(3, result.RepetitionSummaries.Count);
        Assert.All(result.RepetitionSummaries, sm =>
        {
            Assert.Equal(G2SummaryStatus.Valid, sm.Status);
            Assert.Equal(10.0, sm.BatchWallSeconds);
        });
    }

    [Fact]
    public async Task InvalidTimeoutErrorBatches_DoNotHalt_DiagnosticsRetained()
    {
        using var s = NewSession();
        int calls = 0;
        var result = await RunCoordinator(s, oneChild: rep =>
        {
            calls++;
            return rep switch
            {
                1 => Task.FromResult(Child(rep, "INVALID", LogicalStatus.Invalid, batch: Batch(TimedResultStatus.Invalid, 5.0))),
                2 => Task.FromResult(Child(rep, batch: Batch(TimedResultStatus.Timeout, 130.0))),
                _ => Task.FromResult(Child(rep, "ERROR", LogicalStatus.Error, batch: Batch(TimedResultStatus.Error, 7.0))),
            };
        });
        Assert.Equal(3, calls);
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid); // child evidence axes all valid
        var summaries = result.RepetitionSummaries;
        Assert.All(summaries, sm => Assert.Equal(G2SummaryStatus.Incomplete, sm.Status));
        Assert.All(summaries, sm => Assert.Null(sm.BatchWallSeconds));
        Assert.Contains(G2IncompleteReason.InvalidBatch, summaries[0].Reasons);
        Assert.Contains(G2IncompleteReason.TimeoutBatch, summaries[1].Reasons);
        Assert.Contains(G2IncompleteReason.ErrorBatch, summaries[2].Reasons);
        Assert.Equal(5.0, summaries[0].ObservedDiagnosticWallSeconds);
        Assert.Equal(130.0, summaries[1].ObservedDiagnosticWallSeconds);
        Assert.Equal(7.0, summaries[2].ObservedDiagnosticWallSeconds);
    }

    [Theory]
    [InlineData(false, true, "evidence invalid")]
    [InlineData(true, false, "stable artifact capture failed")]
    public async Task EvidenceOrCaptureFailure_Halts_NotAttempted(bool evidence, bool stable, string expectedReason)
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
        var notAttempted = result.RepetitionSummaries.Where(sm => sm.Reasons.Contains(G2IncompleteReason.NotAttemptedDueToHalt)).ToList();
        Assert.Equal(2, notAttempted.Count);
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
            return Task.FromResult(Child(rep, resource: resource, batch: Batch(TimedResultStatus.Valid)));
        });
        Assert.Equal(3, calls);
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.All(result.RepetitionSummaries, sm => Assert.Equal(G2SummaryStatus.Valid, sm.Status));
    }

    [Fact]
    public async Task ZeroBatchTrustworthyForms_IncompleteNoWall()
    {
        using var s = NewSession();
        var result = await RunCoordinator(s, oneChild: rep =>
            Task.FromResult(Child(rep, timedComplete: false, batch: null)));
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.EvidenceValid);
        Assert.All(result.RepetitionSummaries, sm =>
        {
            Assert.Equal(G2SummaryStatus.Incomplete, sm.Status);
            Assert.Null(sm.BatchWallSeconds);
            Assert.Null(sm.ObservedDiagnosticWallSeconds);
            Assert.Contains(G2IncompleteReason.TimedBatchIncomplete, sm.Reasons);
            Assert.Contains(G2IncompleteReason.MissingBatch, sm.Reasons);
        });
    }

    [Fact]
    public async Task CoordinatorArtifactCollision_EvidenceInvalid()
    {
        using var s = NewSession();
        string physical = Path.Combine(s.StagingPath, "graph", "g2", "coordinator.json");
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        File.WriteAllText(physical, "occupied");
        var result = await RunCoordinator(s, oneChild: rep => Task.FromResult(Child(rep, batch: Batch(TimedResultStatus.Valid))));
        Assert.True(result.CoordinatorComplete);
        Assert.False(result.CoordinatorArtifactWritten);
        Assert.True(result.RepetitionSummariesArtifactWritten);
        Assert.False(result.EvidenceValid);
    }

    [Fact]
    public async Task SummaryArtifactCollision_EvidenceInvalid_ChildrenRetained()
    {
        using var s = NewSession();
        string physical = Path.Combine(s.StagingPath, "graph", "g2", "repetition-summaries.json");
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        File.WriteAllText(physical, "occupied");
        var result = await RunCoordinator(s, oneChild: rep => Task.FromResult(Child(rep, batch: Batch(TimedResultStatus.Valid))));
        Assert.True(result.CoordinatorArtifactWritten);
        Assert.False(result.RepetitionSummariesArtifactWritten);
        Assert.False(result.EvidenceValid);
        Assert.Equal(3, result.AttemptedExecutionCount);
        Assert.Equal(3, result.RepetitionSummaries.Count);
    }

    [Fact]
    public async Task DeterministicEvidence()
    {
        using var s = NewSession();
        var result = await RunCoordinator(s, oneChild: rep => Task.FromResult(Child(rep, batch: Batch(TimedResultStatus.Valid))));
        string coordinator = File.ReadAllText(Path.Combine(s.StagingPath, "graph", "g2", "coordinator.json"));
        Assert.Contains("\"planned_execution_count\":3", coordinator);
        Assert.Contains("\"child_evidence_valid\":true", coordinator);
        Assert.Contains("\"batch_status\":\"Valid\"", coordinator);
        string summaries = File.ReadAllText(Path.Combine(s.StagingPath, "graph", "g2", "repetition-summaries.json"));
        Assert.Equal(3, result.RepetitionSummaries.Count);
        Assert.Equal(new[] { 1, 2, 3 }, result.RepetitionSummaries.Select(sm => sm.Repetition).ToArray());
        Assert.Contains("\"expected_per_input_count\":3", summaries);
        Assert.Contains("\"batch_wall_seconds\":10", summaries);
        Assert.DoesNotContain("p50", summaries);
    }
}

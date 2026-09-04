using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class G2CrossSummaryEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-g2cx-" + Guid.NewGuid().ToString("N"));

    public G2CrossSummaryEvidenceTests() => Directory.CreateDirectory(_root);
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

    private static G2RepetitionSummary ValidRep(int rep, double wall)
        => new("G2", rep, G2SummaryStatus.Valid, Array.Empty<G2IncompleteReason>(), 200, 200,
            ServingStatuses.Valid, TimedResultStatus.Valid, wall, wall);

    private static G2RepetitionSummary IncompleteRep(int rep, G2IncompleteReason reason)
        => new("G2", rep, G2SummaryStatus.Incomplete, new[] { reason }, 200, 0, "VALID", TimedResultStatus.Timeout, null, 130.0);

    private static G2RepetitionSummary RawOp(string op, int rep)
        => new(op, rep, G2SummaryStatus.Incomplete, new[] { G2IncompleteReason.NotAttemptedDueToHalt }, 200, 0, null, null, null, null);

    private static G2RunCoordinatorResult Coordinator(bool evidenceValid, params G2RepetitionSummary[] summaries) => new(
        PlannedExecutionCount: 3,
        AttemptedExecutionCount: 3,
        CoordinatorComplete: true,
        EvidenceValid: evidenceValid,
        Halted: false,
        HaltAfterRepetition: null,
        HaltReason: null,
        Executions: Array.Empty<G2ExecutionRecord>(),
        RepetitionSummaries: summaries,
        WatchdogSeconds: 3600,
        CoordinatorArtifactWritten: true,
        RepetitionSummariesArtifactWritten: true);

    private EvidenceStagingSession NewSession()
    {
        string runs = Path.Combine(_root, "runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runs);
        return EvidenceStagingSession.Create(runs, Identity());
    }

    private static string ArtifactPhysical(EvidenceStagingSession session)
        => Path.Combine(session.StagingPath, "graph", "g2", "cross-repetition-summaries.json");

    [Fact]
    public void AllValid_MedianAndReady()
    {
        using var s = NewSession();
        var r = G2CrossSummaryEvidence.Run(s, Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.True(r.CoordinatorComplete);
        Assert.True(r.InputIntegrityValid);
        Assert.True(r.EvidenceValid);
        Assert.True(r.G2ComparisonReady);
        Assert.True(r.CrossArtifactWritten);
        Assert.Equal(G2CrossSummaryStatus.Valid, r.CrossSummary.Status);
        Assert.Equal(20, r.CrossSummary.MedianBatchWallSeconds);
        Assert.Equal(200, r.CrossSummary.ExpectedPerInputCount);
        string text = File.ReadAllText(ArtifactPhysical(s));
        Assert.Contains("\"g2_comparison_ready\":true", text);
        Assert.Contains("\"median_batch_wall_seconds\":20", text);
    }

    [Fact]
    public void LegitTimeout_ReadyFalse_EvidenceTrue()
    {
        using var s = NewSession();
        var r = G2CrossSummaryEvidence.Run(s, Coordinator(true, ValidRep(1, 10), IncompleteRep(2, G2IncompleteReason.TimeoutBatch), ValidRep(3, 30)));
        Assert.True(r.InputIntegrityValid);
        Assert.True(r.EvidenceValid);
        Assert.False(r.G2ComparisonReady);
        Assert.Equal(G2CrossSummaryStatus.Incomplete, r.CrossSummary.Status);
        Assert.Null(r.CrossSummary.MedianBatchWallSeconds);
    }

    [Fact]
    public void MissingRep_IntegrityInvalid_DiagnosticArtifact()
    {
        using var s = NewSession();
        var r = G2CrossSummaryEvidence.Run(s, Coordinator(true, ValidRep(1, 10), ValidRep(3, 30)));
        Assert.False(r.InputIntegrityValid);
        Assert.False(r.EvidenceValid);
        Assert.False(r.G2ComparisonReady);
        Assert.True(r.CrossArtifactWritten);
        Assert.Contains(r.IntegrityProblems, p => p.Code == G2CrossIntegrityCode.MissingRepetitionSummary && p.Repetition == 2);
    }

    [Fact]
    public void ExtraRep4_ReadyTrue_IntegrityInvalid_EvidenceFalse()
    {
        using var s = NewSession();
        var r = G2CrossSummaryEvidence.Run(s, Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30), ValidRep(4, 999)));
        Assert.False(r.InputIntegrityValid);
        Assert.False(r.EvidenceValid);
        Assert.True(r.G2ComparisonReady);
        Assert.Equal(20, r.CrossSummary.MedianBatchWallSeconds);
    }

    [Fact]
    public void UpstreamEvidenceInvalid_OverallFalse_ReadyUnchanged()
    {
        using var s = NewSession();
        var r = G2CrossSummaryEvidence.Run(s, Coordinator(false, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.True(r.InputIntegrityValid);
        Assert.False(r.EvidenceValid);
        Assert.True(r.G2ComparisonReady);
    }

    [Fact]
    public void WriteCollision_EvidenceFalse_ReadyUnchanged()
    {
        using var s = NewSession();
        Directory.CreateDirectory(Path.GetDirectoryName(ArtifactPhysical(s))!);
        File.WriteAllText(ArtifactPhysical(s), "occupied");
        var r = G2CrossSummaryEvidence.Run(s, Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.False(r.CrossArtifactWritten);
        Assert.False(r.EvidenceValid);
        Assert.True(r.G2ComparisonReady);
        Assert.True(r.InputIntegrityValid);
        Assert.Equal("occupied", File.ReadAllText(ArtifactPhysical(s)));
    }

    [Fact]
    public void DeterministicArtifactContent()
    {
        using var s = NewSession();
        var r = G2CrossSummaryEvidence.Run(s, Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        string text = File.ReadAllText(ArtifactPhysical(s));
        Assert.Contains("\"coordinator_complete\":true", text);
        Assert.Contains("\"upstream_evidence_valid\":true", text);
        Assert.Contains("\"operation\":\"G2\"", text);
        Assert.Contains("\"valid_repetition_count\":3", text);
        Assert.DoesNotContain("observed_diagnostic", text);
        Assert.Equal(3, r.CrossSummary.ValidRepetitionCount);
        Assert.Empty(r.CrossSummary.IncompleteRepetitions);
    }
}

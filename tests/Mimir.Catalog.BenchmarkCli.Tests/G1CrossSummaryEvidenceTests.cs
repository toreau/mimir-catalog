using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class G1CrossSummaryEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-g1cx-" + Guid.NewGuid().ToString("N"));

    public G1CrossSummaryEvidenceTests() => Directory.CreateDirectory(_root);
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

    private static GraphWorkload Workload(string stratum = "Degree1")
    {
        var probes = new List<GraphProbe> { new("G1", 0, stratum, true, 1000) };
        return new GraphWorkload
        {
            Probes = probes,
            Expected = new Dictionary<(string, long), GraphExpected> { [("G1", 0L)] = new("G1", 0, true, 0, 1, "d") },
        };
    }

    private static G1SummaryMetrics Metrics(double v) => new(1, v, v, v, v, v, v, v, 1);

    private static G1RepetitionSummary ValidRep(int rep, double v)
        => new("G1", "Degree1", rep, G1SummaryStatus.Valid, Array.Empty<G1IncompleteReason>(), 1, 1, 1, 0, 0, 0, Metrics(v));

    private static G1RepetitionSummary IncompleteRep(int rep, G1IncompleteReason reason)
        => new("G1", "Degree1", rep, G1SummaryStatus.Incomplete, new[] { reason }, 1, 0, 0, 0, 0, 0, null);

    private static G1RunCoordinatorResult Coordinator(bool evidenceValid, params G1RepetitionSummary[] summaries) => new(
        PlannedExecutionCount: 3,
        AttemptedExecutionCount: 3,
        CoordinatorComplete: true,
        EvidenceValid: evidenceValid,
        Halted: false,
        HaltAfterRepetition: null,
        HaltReason: null,
        Executions: Array.Empty<G1ExecutionRecord>(),
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
        => Path.Combine(session.StagingPath, "graph", "g1", "cross-repetition-summaries.json");

    [Fact]
    public void AllValid_ReadyTrue_EvidenceTrue()
    {
        using var s = NewSession();
        var r = G1CrossSummaryEvidence.Run(s, Workload(), Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.True(r.CoordinatorComplete);
        Assert.True(r.InputIntegrityValid);
        Assert.True(r.EvidenceValid);
        Assert.True(r.G1ComparisonReady);
        Assert.True(r.CrossArtifactWritten);
        var cross = Assert.Single(r.CrossSummaries);
        Assert.Equal(G1CrossSummaryStatus.Valid, cross.Status);
        Assert.Equal(20, cross.Metrics!.MinSeconds);
        string text = File.ReadAllText(ArtifactPhysical(s));
        Assert.Contains("\"g1_comparison_ready\":true", text);
        Assert.Contains("\"input_integrity_valid\":true", text);
    }

    [Fact]
    public void LegitIncomplete_ReadyFalse_EvidenceTrue()
    {
        using var s = NewSession();
        var r = G1CrossSummaryEvidence.Run(s, Workload(),
            Coordinator(true, ValidRep(1, 10), IncompleteRep(2, G1IncompleteReason.TimeoutSample), ValidRep(3, 30)));
        Assert.True(r.InputIntegrityValid);
        Assert.True(r.EvidenceValid); // benchmark-incomplete but structurally valid evidence
        Assert.False(r.G1ComparisonReady);
        Assert.Equal(G1CrossSummaryStatus.Incomplete, Assert.Single(r.CrossSummaries).Status);
    }

    [Fact]
    public void MissingRep_IntegrityInvalid_DiagnosticArtifact()
    {
        using var s = NewSession();
        var r = G1CrossSummaryEvidence.Run(s, Workload(), Coordinator(true, ValidRep(1, 10), ValidRep(3, 30)));
        Assert.False(r.InputIntegrityValid);
        Assert.False(r.EvidenceValid);
        Assert.False(r.G1ComparisonReady);
        Assert.True(r.CrossArtifactWritten);
        Assert.Contains(r.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.MissingRepetitionSummary && p.Repetition == 2);
        string text = File.ReadAllText(ArtifactPhysical(s));
        Assert.Contains("MissingRepetitionSummary", text);
        Assert.Contains("\"input_integrity_valid\":false", text);
    }

    [Fact]
    public void ExtraRep4_ReadyTrue_IntegrityInvalid_EvidenceFalse()
    {
        using var s = NewSession();
        var r = G1CrossSummaryEvidence.Run(s, Workload(),
            Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30), ValidRep(4, 999)));
        Assert.False(r.InputIntegrityValid);
        Assert.False(r.EvidenceValid);
        Assert.True(r.G1ComparisonReady); // expected matrix untouched
        var cross = Assert.Single(r.CrossSummaries);
        Assert.Equal(G1CrossSummaryStatus.Valid, cross.Status);
        Assert.Equal(20, cross.Metrics!.MinSeconds);
        string text = File.ReadAllText(ArtifactPhysical(s));
        Assert.Contains("\"g1_comparison_ready\":true", text);
        Assert.Contains("\"input_integrity_valid\":false", text);
    }

    [Fact]
    public void UpstreamEvidenceInvalid_OverallFalse_ReadyUnchanged()
    {
        using var s = NewSession();
        var r = G1CrossSummaryEvidence.Run(s, Workload(), Coordinator(false, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.True(r.InputIntegrityValid);
        Assert.False(r.EvidenceValid);
        Assert.True(r.G1ComparisonReady);
    }

    [Fact]
    public void WriteCollision_EvidenceFalse_ReadyAndCalculationUnchanged()
    {
        using var s = NewSession();
        Directory.CreateDirectory(Path.GetDirectoryName(ArtifactPhysical(s))!);
        File.WriteAllText(ArtifactPhysical(s), "occupied");
        var r = G1CrossSummaryEvidence.Run(s, Workload(), Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.False(r.CrossArtifactWritten);
        Assert.False(r.EvidenceValid);
        Assert.True(r.G1ComparisonReady);
        Assert.True(r.InputIntegrityValid);
        Assert.Equal("occupied", File.ReadAllText(ArtifactPhysical(s)));
    }

    [Fact]
    public void DeterministicArtifactContent()
    {
        using var s = NewSession();
        var r = G1CrossSummaryEvidence.Run(s, Workload(), Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.True(r.EvidenceValid);
        string text = File.ReadAllText(ArtifactPhysical(s));
        Assert.Contains("\"coordinator_complete\":true", text);
        Assert.Contains("\"upstream_evidence_valid\":true", text);
        Assert.Contains("\"valid_cross_summary_count\":1", text);
        Assert.Contains("\"operation\":\"G1\"", text);
        Assert.Contains("\"stratum\":\"Degree1\"", text);
    }
}

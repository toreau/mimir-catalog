using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class ServingCrossSummaryEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-cse-" + Guid.NewGuid().ToString("N"));

    public ServingCrossSummaryEvidenceTests() => Directory.CreateDirectory(_root);
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

    private static ServingWorkload Workload()
    {
        var probes = new List<ServingProbe> { new("S1", 1, "Hit", true, 101, null, null) };
        return new ServingWorkload
        {
            Probes = probes,
            Expected = new Dictionary<(string, long), ServingExpected> { [("S1", 1)] = new("S1", 1, true, 1, "d") },
        };
    }

    private static ServingSummaryMetrics Metrics(double v) => new(1, v, v, v, v, v, v, v, 1);

    private static ServingRepetitionSummary ValidRep(int rep, double v)
        => new("S1", "Hit", rep, ServingSummaryStatus.Valid, Array.Empty<ServingIncompleteReason>(),
            1, 1, 1, 0, 0, 0, Metrics(v));

    private static ServingRepetitionSummary IncompleteRep(int rep, ServingIncompleteReason reason)
        => new("S1", "Hit", rep, ServingSummaryStatus.Incomplete, new[] { reason }, 1, 0, 0, 0, 0, 0, null);

    private static ServingRepetitionSummary Raw(int rep, long expected, ServingSummaryStatus status,
        IReadOnlyList<ServingIncompleteReason> reasons, ServingSummaryMetrics? metrics)
        => new("S1", "Hit", rep, status, reasons, expected, expected, expected, 0, 0, 0, metrics);

    private static ServingRunCoordinatorResult Coordinator(bool evidenceValid, params ServingRepetitionSummary[] summaries) => new(
        PlannedExecutionCount: 15,
        AttemptedExecutionCount: 15,
        CoordinatorComplete: true,
        EvidenceValid: evidenceValid,
        Halted: false,
        HaltAfterOperation: null,
        HaltAfterRepetition: null,
        HaltReason: null,
        Executions: Array.Empty<ServingExecutionRecord>(),
        RepetitionSummaries: summaries,
        WatchdogSeconds: 3600);

    private EvidenceStagingSession NewSession()
    {
        string runs = Path.Combine(_root, "runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runs);
        return EvidenceStagingSession.Create(runs, Identity());
    }

    private static string CrossPhysical(EvidenceStagingSession session)
        => Path.Combine(session.StagingPath, "serving", "cross-repetition-summaries.json");

    [Fact]
    public void AllValid_ReadyTrue_EvidenceTrue_ArtifactWritten()
    {
        using var s = NewSession();
        var result = ServingCrossSummaryEvidence.Run(s, Workload(), Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.True(result.CoordinatorComplete);
        Assert.True(result.InputIntegrityValid);
        Assert.True(result.EvidenceValid);
        Assert.True(result.ServingComparisonReady);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(ServingSummaryStatus.Valid, cross.Status);
        Assert.Equal(20, cross.Metrics!.MinSeconds);
        string text = File.ReadAllText(CrossPhysical(s));
        Assert.Contains("\"serving_comparison_ready\":true", text);
        Assert.Contains("\"input_integrity_valid\":true", text);
    }

    [Fact]
    public void LegitIncomplete_ReadyFalse_EvidenceTrue()
    {
        using var s = NewSession();
        var result = ServingCrossSummaryEvidence.Run(s, Workload(), Coordinator(true, ValidRep(1, 10), IncompleteRep(2, ServingIncompleteReason.TimeoutSample), ValidRep(3, 30)));
        Assert.True(result.InputIntegrityValid);
        Assert.True(result.EvidenceValid);
        Assert.False(result.ServingComparisonReady);
        Assert.Equal(ServingSummaryStatus.Incomplete, Assert.Single(result.CrossSummaries).Status);
        string text = File.ReadAllText(CrossPhysical(s));
        Assert.Contains("\"serving_comparison_ready\":false", text);
    }

    [Fact]
    public void ExtraRep4_ReadyTrue_IntegrityInvalid_EvidenceFalse()
    {
        using var s = NewSession();
        var result = ServingCrossSummaryEvidence.Run(s, Workload(),
            Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30), ValidRep(4, 999)));
        Assert.False(result.InputIntegrityValid);
        Assert.False(result.EvidenceValid); // input integrity gates final EvidenceValid
        Assert.True(result.ServingComparisonReady); // expected group still Valid
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.UnexpectedRepetitionNumber);
        string text = File.ReadAllText(CrossPhysical(s));
        Assert.Contains("\"serving_comparison_ready\":true", text);
        Assert.Contains("\"input_integrity_valid\":false", text);
    }

    [Fact]
    public void UpstreamEvidenceInvalid_OverallFalse_ReadyUnchanged()
    {
        using var s = NewSession();
        var result = ServingCrossSummaryEvidence.Run(s, Workload(), Coordinator(false, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.True(result.InputIntegrityValid);
        Assert.False(result.EvidenceValid);
        Assert.True(result.ServingComparisonReady);
        string text = File.ReadAllText(CrossPhysical(s));
        Assert.Contains("\"upstream_evidence_valid\":false", text);
    }

    [Fact]
    public void MissingRep_IntegrityInvalid_ReadyFalse_DiagnosticArtifact()
    {
        using var s = NewSession();
        var result = ServingCrossSummaryEvidence.Run(s, Workload(), Coordinator(true, ValidRep(1, 10), ValidRep(3, 30)));
        Assert.False(result.InputIntegrityValid);
        Assert.False(result.EvidenceValid);
        Assert.False(result.ServingComparisonReady);
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.MissingRepetitionSummary && p.Repetition == 2);
        string text = File.ReadAllText(CrossPhysical(s)); // diagnostic evidence still written
        Assert.Contains("\"input_integrity_valid\":false", text);
        Assert.Contains("MissingRepetitionSummary", text);
    }

    [Fact]
    public void WriteCollision_EvidenceFalse_ReadyAndCalculationUnchanged()
    {
        using var s = NewSession();
        Directory.CreateDirectory(Path.GetDirectoryName(CrossPhysical(s))!);
        File.WriteAllText(CrossPhysical(s), "occupied");
        var result = ServingCrossSummaryEvidence.Run(s, Workload(), Coordinator(true, ValidRep(1, 10), ValidRep(2, 20), ValidRep(3, 30)));
        Assert.False(result.EvidenceValid);
        Assert.True(result.ServingComparisonReady);
        Assert.True(result.InputIntegrityValid);
        Assert.Equal("occupied", File.ReadAllText(CrossPhysical(s)));
    }

    [Fact]
    public void ZeroExpectedKeys_FailsClosed()
    {
        using var s = NewSession();
        var emptyWorkload = new ServingWorkload { Probes = new List<ServingProbe>(), Expected = new Dictionary<(string, long), ServingExpected>() };
        var result = ServingCrossSummaryEvidence.Run(s, emptyWorkload, Coordinator(true));
        Assert.False(result.InputIntegrityValid);
        Assert.False(result.EvidenceValid);
        Assert.False(result.ServingComparisonReady);
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.NoExpectedCrossSummaries);
    }
}

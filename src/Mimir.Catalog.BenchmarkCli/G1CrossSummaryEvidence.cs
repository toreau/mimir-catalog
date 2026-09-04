using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Evidence;

namespace Mimir.Catalog.BenchmarkCli;

public sealed record G1CrossCloseoutResult(
    bool CoordinatorComplete,
    bool InputIntegrityValid,
    bool EvidenceValid,
    bool G1ComparisonReady,
    IReadOnlyList<G1CrossIntegrityProblem> IntegrityProblems,
    IReadOnlyList<G1CrossSummary> CrossSummaries,
    bool CrossArtifactWritten);

/// <summary>
/// Binds the closed G1 repetition summaries to the cross calculator and persists
/// one deterministic cross artifact into the caller-owned staging session. No
/// process/resource/sample work; no publication.
/// </summary>
public static class G1CrossSummaryEvidence
{
    public static G1CrossCloseoutResult Run(
        EvidenceStagingSession session,
        GraphWorkload workload,
        G1RunCoordinatorResult coordinator)
    {
        var calc = G1CrossSummaryCalculator.Calculate(workload, coordinator.RepetitionSummaries);

        bool writeOk = true;
        try
        {
            session.WriteText("graph/g1/cross-repetition-summaries.json", JsonOf(new
            {
                coordinator_complete = coordinator.CoordinatorComplete,
                upstream_evidence_valid = coordinator.EvidenceValid,
                input_integrity_valid = calc.InputIntegrityValid,
                integrity_problems = calc.IntegrityProblems.Select(p => new
                {
                    operation = p.Operation,
                    stratum = p.Stratum,
                    repetition = p.Repetition,
                    code = p.Code.ToString(),
                }),
                g1_comparison_ready = calc.G1ComparisonReady,
                expected_cross_summary_count = calc.CrossSummaries.Count,
                valid_cross_summary_count = calc.CrossSummaries.Count(c => c.Status == G1CrossSummaryStatus.Valid),
                summaries = calc.CrossSummaries.Select(c => new
                {
                    operation = c.Operation,
                    stratum = c.Stratum,
                    status = c.Status.ToString(),
                    expected_count = c.ExpectedCount,
                    valid_repetition_count = c.ValidRepetitionCount,
                    incomplete_repetitions = c.IncompleteRepetitions,
                    metrics = c.Metrics is null ? null : new
                    {
                        count = c.Metrics.Count,
                        min_seconds = c.Metrics.MinSeconds,
                        p50_seconds = c.Metrics.P50Seconds,
                        p90_seconds = c.Metrics.P90Seconds,
                        p95_seconds = c.Metrics.P95Seconds,
                        p99_seconds = c.Metrics.P99Seconds,
                        max_seconds = c.Metrics.MaxSeconds,
                        mean_seconds = c.Metrics.MeanSeconds,
                        throughput_per_second = c.Metrics.ThroughputPerSecond,
                    },
                }),
            }));
        }
        catch
        {
            writeOk = false;
        }

        return new G1CrossCloseoutResult(
            CoordinatorComplete: coordinator.CoordinatorComplete,
            InputIntegrityValid: calc.InputIntegrityValid,
            EvidenceValid: coordinator.EvidenceValid && calc.InputIntegrityValid && writeOk,
            G1ComparisonReady: calc.G1ComparisonReady,
            IntegrityProblems: calc.IntegrityProblems,
            CrossSummaries: calc.CrossSummaries,
            CrossArtifactWritten: writeOk);
    }

    private static string JsonOf(object value) => System.Text.Json.JsonSerializer.Serialize(value, value.GetType(),
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
}

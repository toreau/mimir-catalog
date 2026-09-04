using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Evidence;

namespace Mimir.Catalog.BenchmarkCli;

public sealed record ServingCrossCloseoutResult(
    bool CoordinatorComplete,
    bool InputIntegrityValid,
    bool EvidenceValid,
    bool ServingComparisonReady,
    IReadOnlyList<ServingIntegrityProblem> IntegrityProblems,
    IReadOnlyList<ServingCrossSummary> CrossSummaries);

/// <summary>
/// Binds the closed b.2a in-memory repetition summaries to the cross-repetition
/// calculator and persists one deterministic cross-summary artifact into the
/// caller-owned staging session. No process/resource/sample work; no publication.
/// </summary>
public static class ServingCrossSummaryEvidence
{
    public static ServingCrossCloseoutResult Run(
        EvidenceStagingSession session,
        ServingWorkload workload,
        ServingRunCoordinatorResult coordinator)
    {
        var calc = ServingCrossSummaryCalculator.Calculate(workload, coordinator.RepetitionSummaries);

        bool writeOk = true;
        try
        {
            session.WriteText("serving/cross-repetition-summaries.json", JsonOf(new
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
                serving_comparison_ready = calc.ServingComparisonReady,
                expected_cross_summary_count = calc.CrossSummaries.Count,
                valid_cross_summary_count = calc.CrossSummaries.Count(c => c.Status == ServingSummaryStatus.Valid),
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

        return new ServingCrossCloseoutResult(
            CoordinatorComplete: coordinator.CoordinatorComplete,
            InputIntegrityValid: calc.InputIntegrityValid,
            EvidenceValid: coordinator.EvidenceValid && calc.InputIntegrityValid && writeOk,
            ServingComparisonReady: calc.ServingComparisonReady,
            IntegrityProblems: calc.IntegrityProblems,
            CrossSummaries: calc.CrossSummaries);
    }

    private static string JsonOf(object value) => System.Text.Json.JsonSerializer.Serialize(value, value.GetType(),
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
}

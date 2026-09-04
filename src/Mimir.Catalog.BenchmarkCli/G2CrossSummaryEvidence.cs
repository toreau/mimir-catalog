using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Evidence;

namespace Mimir.Catalog.BenchmarkCli;

public sealed record G2CrossCloseoutResult(
    bool CoordinatorComplete,
    bool InputIntegrityValid,
    bool EvidenceValid,
    bool G2ComparisonReady,
    IReadOnlyList<G2CrossIntegrityProblem> IntegrityProblems,
    G2CrossSummary CrossSummary,
    bool CrossArtifactWritten);

/// <summary>
/// Binds the closed G2 repetition summaries to the cross calculator and persists
/// one deterministic cross artifact into the caller-owned staging session. No
/// workload/raw reload, no process/resource/sample work, no publication.
/// </summary>
public static class G2CrossSummaryEvidence
{
    public static G2CrossCloseoutResult Run(
        EvidenceStagingSession session,
        G2RunCoordinatorResult coordinator)
    {
        var calc = G2CrossSummaryCalculator.Calculate(coordinator.RepetitionSummaries);

        bool writeOk = true;
        try
        {
            session.WriteText("graph/g2/cross-repetition-summaries.json", JsonOf(new
            {
                coordinator_complete = coordinator.CoordinatorComplete,
                upstream_evidence_valid = coordinator.EvidenceValid,
                input_integrity_valid = calc.InputIntegrityValid,
                integrity_problems = calc.IntegrityProblems.Select(p => new
                {
                    operation = p.Operation,
                    repetition = p.Repetition,
                    code = p.Code.ToString(),
                }),
                g2_comparison_ready = calc.G2ComparisonReady,
                summary = new
                {
                    operation = calc.CrossSummary.Operation,
                    status = calc.CrossSummary.Status.ToString(),
                    expected_per_input_count = calc.CrossSummary.ExpectedPerInputCount,
                    valid_repetition_count = calc.CrossSummary.ValidRepetitionCount,
                    incomplete_repetitions = calc.CrossSummary.IncompleteRepetitions,
                    median_batch_wall_seconds = calc.CrossSummary.MedianBatchWallSeconds,
                },
            }));
        }
        catch
        {
            writeOk = false;
        }

        return new G2CrossCloseoutResult(
            CoordinatorComplete: coordinator.CoordinatorComplete,
            InputIntegrityValid: calc.InputIntegrityValid,
            EvidenceValid: coordinator.EvidenceValid && calc.InputIntegrityValid && writeOk,
            G2ComparisonReady: calc.G2ComparisonReady,
            IntegrityProblems: calc.IntegrityProblems,
            CrossSummary: calc.CrossSummary,
            CrossArtifactWritten: writeOk);
    }

    private static string JsonOf(object value) => System.Text.Json.JsonSerializer.Serialize(value, value.GetType(),
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
}

using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;

namespace Mimir.Catalog.BenchmarkCli;

public sealed record G2ExecutionRecord(
    int Repetition,
    bool EvidenceValid,
    bool RegisteredStableArtifacts,
    string ProcessOutcome,
    string ResourceStatus,
    string? EnvelopeStatus,
    bool TimedBatchComplete,
    bool BatchPresent,
    string? BatchStatus,
    int ParentPerInputCount);

public sealed record G2RunCoordinatorResult(
    int PlannedExecutionCount,
    int AttemptedExecutionCount,
    bool CoordinatorComplete,
    bool EvidenceValid,
    bool Halted,
    int? HaltAfterRepetition,
    string? HaltReason,
    IReadOnlyList<G2ExecutionRecord> Executions,
    IReadOnlyList<G2RepetitionSummary> RepetitionSummaries,
    double WatchdogSeconds,
    bool CoordinatorArtifactWritten,
    bool RepetitionSummariesArtifactWritten);

/// <summary>
/// Deterministic 3-repetition G2 coordinator (rep 1..3 serial, one measured
/// Batch per repetition). Calls the closed one-child G2 orchestrator once per
/// repetition with one shared watchdog, aborts on evidence/capture failure and
/// persists coordinator + repetition-summary evidence. No cross median/readiness,
/// no G1 work, no publication.
/// </summary>
public static class G2RunCoordinator
{
    public static async Task<G2RunCoordinatorResult> RunAsync(
        EvidenceStagingSession session,
        string candidatePath,
        string workloadPath,
        G2Workload workload,
        Func<string, ProcessInvocation> childInvocationFactory,
        TimeSpan watchdog,
        Func<int, Task<G2ChildEvidenceResult>>? oneChild = null,
        Action<int, TimeSpan>? callProbe = null)
    {
        Func<int, Task<G2ChildEvidenceResult>> runOne = oneChild ?? (rep =>
            G2ChildOrchestrator.RunAsync(session, rep, candidatePath, workloadPath, workload, childInvocationFactory, watchdog));

        int expectedPerInput = workload.Concepts.Count;
        var attempted = new List<G2ChildEvidenceResult>();
        var records = new List<G2ExecutionRecord>();
        bool halted = false;
        int? haltAfter = null;
        string? haltReason = null;

        for (int rep = 1; rep <= 3; rep++)
        {
            if (halted) break;
            callProbe?.Invoke(rep, watchdog);
            var child = await runOne(rep).ConfigureAwait(false);
            attempted.Add(child);
            records.Add(new G2ExecutionRecord(
                rep,
                child.EvidenceValid,
                child.RegisteredStableArtifacts,
                child.ProcessOutcome.ToString(),
                child.ResourceStatus.ToString(),
                child.Envelope?.CorrectnessStatus,
                child.TimedBatchComplete,
                child.Batch is not null,
                child.Batch?.Status.ToString(),
                child.PerInput.Count));
            if (!child.EvidenceValid || !child.RegisteredStableArtifacts)
            {
                halted = true;
                haltAfter = rep;
                haltReason = !child.EvidenceValid ? "evidence invalid" : "stable artifact capture failed";
            }
        }

        bool childEvidenceValid = attempted.Count > 0 && attempted.All(c => c.EvidenceValid && c.RegisteredStableArtifacts);

        var summaries = new List<G2RepetitionSummary>();
        for (int rep = 1; rep <= 3; rep++)
        {
            var child = attempted.FirstOrDefault(c => c.Repetition == rep);
            var snapshot = child is null ? null : ToSnapshot(child);
            summaries.Add(G2SummaryCalculator.Summarize(rep, expectedPerInput, snapshot));
        }

        bool coordinatorOk = WriteOwned(session, "graph/g2/coordinator.json", JsonOf(new
        {
            planned_execution_count = 3,
            attempted_execution_count = attempted.Count,
            coordinator_complete = !halted,
            child_evidence_valid = childEvidenceValid,
            watchdog_seconds = watchdog.TotalSeconds,
            halted,
            halt_after_repetition = haltAfter,
            halt_reason = haltReason,
            executions = records.Select(r => new
            {
                repetition = r.Repetition,
                evidence_valid = r.EvidenceValid,
                registered_stable_artifacts = r.RegisteredStableArtifacts,
                process_outcome = r.ProcessOutcome,
                resource_status = r.ResourceStatus,
                envelope_status = r.EnvelopeStatus,
                timed_batch_complete = r.TimedBatchComplete,
                batch_present = r.BatchPresent,
                batch_status = r.BatchStatus,
                parent_per_input_count = r.ParentPerInputCount,
            }),
        }));

        bool summariesOk = WriteOwned(session, "graph/g2/repetition-summaries.json", JsonOf(
            summaries.Select(s => new
            {
                operation = s.Operation,
                repetition = s.Repetition,
                status = s.Status.ToString(),
                reasons = s.Reasons.Select(r => r.ToString()),
                expected_per_input_count = s.ExpectedPerInputCount,
                observed_per_input_count = s.ObservedPerInputCount,
                child_correctness = s.ChildCorrectness,
                timed_status = s.TimedStatus?.ToString(),
                batch_wall_seconds = s.BatchWallSeconds,
                observed_diagnostic_wall_seconds = s.ObservedDiagnosticWallSeconds,
            })));

        return new G2RunCoordinatorResult(
            PlannedExecutionCount: 3,
            AttemptedExecutionCount: attempted.Count,
            CoordinatorComplete: !halted,
            EvidenceValid: childEvidenceValid && coordinatorOk && summariesOk,
            Halted: halted,
            HaltAfterRepetition: haltAfter,
            HaltReason: haltReason,
            Executions: records,
            RepetitionSummaries: summaries,
            WatchdogSeconds: watchdog.TotalSeconds,
            CoordinatorArtifactWritten: coordinatorOk,
            RepetitionSummariesArtifactWritten: summariesOk);
    }

    private static bool WriteOwned(EvidenceStagingSession session, string rel, string text)
    {
        try { session.WriteText(rel, text); return true; }
        catch { return false; }
    }

    private static string JsonOf(object value) => System.Text.Json.JsonSerializer.Serialize(value, value.GetType(),
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    private static G2ChildSnapshot ToSnapshot(G2ChildEvidenceResult child) => new(
        child.EvidenceValid,
        child.RegisteredStableArtifacts,
        child.ProcessOutcome == ProcessOutcome.CompletedProtocolResult,
        child.Envelope?.CorrectnessStatus ?? "",
        child.TimedBatchComplete,
        child.Batch,
        child.PerInput.Count);
}

using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;

namespace Mimir.Catalog.BenchmarkCli;

public sealed record G1ExecutionRecord(
    int Repetition,
    bool EvidenceValid,
    bool RegisteredStableArtifacts,
    string ProcessOutcome,
    string ResourceStatus,
    string? EnvelopeStatus,
    bool MeasuredSequenceComplete,
    int ParentSampleCount);

public sealed record G1RunCoordinatorResult(
    int PlannedExecutionCount,
    int AttemptedExecutionCount,
    bool CoordinatorComplete,
    bool EvidenceValid,
    bool Halted,
    int? HaltAfterRepetition,
    string? HaltReason,
    IReadOnlyList<G1ExecutionRecord> Executions,
    IReadOnlyList<G1RepetitionSummary> RepetitionSummaries,
    double WatchdogSeconds,
    bool CoordinatorArtifactWritten,
    bool RepetitionSummariesArtifactWritten);

/// <summary>
/// Deterministic 3-repetition G1 coordinator (rep 1..3 serial). Calls the closed
/// one-child G1 orchestrator once per repetition with one shared watchdog, aborts
/// on any evidence/capture failure and persists coordinator + repetition-summary
/// evidence into the caller-owned staging session. No cross-repetition
/// aggregation, no G2 work, no publication.
/// </summary>
public static class G1RunCoordinator
{
    public static async Task<G1RunCoordinatorResult> RunAsync(
        EvidenceStagingSession session,
        string candidatePath,
        string workloadPath,
        GraphWorkload workload,
        Func<string, ProcessInvocation> childInvocationFactory,
        TimeSpan watchdog,
        Func<int, Task<G1ChildEvidenceResult>>? oneChild = null,
        Action<int, TimeSpan>? callProbe = null)
    {
        Func<int, Task<G1ChildEvidenceResult>> runOne = oneChild ?? (rep =>
            G1ChildOrchestrator.RunAsync(session, rep, candidatePath, workloadPath, workload, childInvocationFactory, watchdog));

        var expectedStrata = ExpectedStrata(workload);
        var attempted = new List<G1ChildEvidenceResult>();
        var records = new List<G1ExecutionRecord>();
        bool halted = false;
        int? haltAfter = null;
        string? haltReason = null;

        for (int rep = 1; rep <= 3; rep++)
        {
            if (halted) break;
            callProbe?.Invoke(rep, watchdog);
            var child = await runOne(rep).ConfigureAwait(false);
            attempted.Add(child);
            records.Add(new G1ExecutionRecord(
                rep,
                child.EvidenceValid,
                child.RegisteredStableArtifacts,
                child.ProcessOutcome.ToString(),
                child.ResourceStatus.ToString(),
                child.Envelope?.Status.ToString(),
                child.MeasuredSequenceComplete,
                child.ParentSamples.Count));
            if (!child.EvidenceValid || !child.RegisteredStableArtifacts)
            {
                halted = true;
                haltAfter = rep;
                haltReason = !child.EvidenceValid ? "evidence invalid" : "stable artifact capture failed";
            }
        }

        bool childEvidenceValid = attempted.Count > 0 && attempted.All(c => c.EvidenceValid && c.RegisteredStableArtifacts);

        var summaries = new List<G1RepetitionSummary>();
        foreach (string stratum in expectedStrata.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            long expectedCount = expectedStrata[stratum];
            for (int rep = 1; rep <= 3; rep++)
            {
                var child = attempted.FirstOrDefault(c => c.Repetition == rep);
                var snapshot = child is null ? null : ToSnapshot(child);
                summaries.Add(G1SummaryCalculator.Summarize(stratum, rep, expectedCount, snapshot));
            }
        }

        bool coordinatorOk = WriteOwned(session, "graph/g1/coordinator.json", JsonOf(new
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
                measured_sequence_complete = r.MeasuredSequenceComplete,
                parent_sample_count = r.ParentSampleCount,
            }),
        }));

        bool summariesOk = WriteOwned(session, "graph/g1/repetition-summaries.json", JsonOf(
            summaries.Select(s => new
            {
                operation = s.Operation,
                stratum = s.Stratum,
                repetition = s.Repetition,
                status = s.Status.ToString(),
                reasons = s.Reasons.Select(r => r.ToString()),
                expected_count = s.ExpectedCount,
                observed_count = s.ObservedCount,
                valid_count = s.ValidCount,
                invalid_count = s.InvalidCount,
                timeout_count = s.TimeoutCount,
                error_count = s.ErrorCount,
                metrics = s.Metrics is null ? null : new
                {
                    count = s.Metrics.Count,
                    min_seconds = s.Metrics.MinSeconds,
                    p50_seconds = s.Metrics.P50Seconds,
                    p90_seconds = s.Metrics.P90Seconds,
                    p95_seconds = s.Metrics.P95Seconds,
                    p99_seconds = s.Metrics.P99Seconds,
                    max_seconds = s.Metrics.MaxSeconds,
                    mean_seconds = s.Metrics.MeanSeconds,
                    throughput_per_second = s.Metrics.ThroughputPerSecond,
                },
            })));

        return new G1RunCoordinatorResult(
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

    private static G1ChildSnapshot ToSnapshot(G1ChildEvidenceResult child) => new(
        child.EvidenceValid,
        child.RegisteredStableArtifacts,
        child.ProcessOutcome == ProcessOutcome.CompletedProtocolResult,
        child.Envelope?.CorrectnessStatus ?? "",
        child.MeasuredSequenceComplete,
        child.ParentSamples);

    private static Dictionary<string, long> ExpectedStrata(GraphWorkload workload)
        => workload.Probes
            .Where(p => p.Op == "G1" && p.Measured)
            .GroupBy(p => p.Stratum)
            .ToDictionary(g => g.Key, g => g.LongCount());
}

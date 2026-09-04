using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;

namespace Mimir.Catalog.BenchmarkCli;

public sealed record ServingExecutionRecord(
    string Operation,
    int Repetition,
    bool EvidenceValid,
    bool RegisteredStableArtifacts,
    string ProcessOutcome,
    string ResourceStatus,
    string? EnvelopeStatus,
    bool MeasuredSequenceComplete,
    int ParentSampleCount);

public sealed record ServingRunCoordinatorResult(
    int PlannedExecutionCount,
    int AttemptedExecutionCount,
    bool CoordinatorComplete,
    bool EvidenceValid,
    bool Halted,
    string? HaltAfterOperation,
    int? HaltAfterRepetition,
    string? HaltReason,
    IReadOnlyList<ServingExecutionRecord> Executions,
    IReadOnlyList<ServingRepetitionSummary> RepetitionSummaries,
    double WatchdogSeconds);

/// <summary>
/// Deterministic 15-unit serving run coordinator (S1r1..S5r3). Calls the frozen
/// one-child orchestrator sequentially with one shared watchdog, aborts on any
/// evidence/integrity failure, and persists coordinator + repetition-summary
/// evidence into the caller-owned staging session. No publication, no
/// cross-repetition aggregation.
/// </summary>
public static class ServingRunCoordinator
{
    private static readonly string[] Operations = { "S1", "S2", "S3", "S4", "S5" };

    public static async Task<ServingRunCoordinatorResult> RunAsync(
        EvidenceStagingSession session,
        string candidatePath,
        string workloadPath,
        ServingWorkload workload,
        Func<string, ProcessInvocation> childInvocationFactory,
        TimeSpan watchdog,
        Func<string, int, ServingWorkload, Task<ServingChildEvidenceResult>>? oneChild = null,
        Action<string, int, TimeSpan>? callProbe = null)
    {
        Func<string, int, ServingWorkload, Task<ServingChildEvidenceResult>> runOne = oneChild ?? ((op, rep, wl) =>
            ServingChildOrchestrator.RunAsync(session, op, rep, candidatePath, workloadPath, wl, childInvocationFactory, watchdog));

        var strata = ExpectedStrata(workload);
        var attempted = new List<ServingChildEvidenceResult>();
        var records = new List<ServingExecutionRecord>();
        bool halted = false;
        string? haltAfterOp = null;
        int? haltAfterRep = null;
        string? haltReason = null;

        foreach (string op in Operations)
        {
            if (halted) break;
            foreach (int rep in Enumerable.Range(1, 3))
            {
                callProbe?.Invoke(op, rep, watchdog);
                var child = await runOne(op, rep, workload).ConfigureAwait(false);
                attempted.Add(child);
                records.Add(ToRecord(child));
                if (child.EvidenceValid != true || child.RegisteredStableArtifacts != true)
                {
                    halted = true;
                    haltAfterOp = op;
                    haltAfterRep = rep;
                    haltReason = child.EvidenceValid == false ? "evidence invalid" : "stable artifact capture failed";
                    break;
                }
            }
        }

        bool computedValid = attempted.Count > 0 && attempted.All(c => c.EvidenceValid && c.RegisteredStableArtifacts);

        var summaries = new List<ServingRepetitionSummary>();
        foreach (string op in Operations)
        {
            if (!strata.TryGetValue(op, out var strataForOp)) continue;
            foreach ((string stratum, long expectedCount) in strataForOp.OrderBy(s => s.Item1, StringComparer.Ordinal))
            {
                foreach (int rep in Enumerable.Range(1, 3))
                {
                    var child = attempted.FirstOrDefault(c => c.Operation == op && c.Repetition == rep);
                    var snapshot = child is null ? null : ToSnapshot(child);
                    summaries.Add(ServingSummaryCalculator.Summarize(op, stratum, rep, expectedCount, snapshot));
                }
            }
        }

        bool coordinatorWriteOk = WriteOwned(session, "serving/coordinator.json", JsonOf(new
        {
            planned_execution_count = 15,
            attempted_execution_count = attempted.Count,
            coordinator_complete = !halted,
            evidence_valid = computedValid,
            watchdog_seconds = watchdog.TotalSeconds,
            halted,
            halt_after_operation = haltAfterOp,
            halt_after_repetition = haltAfterRep,
            halt_reason = haltReason,
            executions = records.Select(r => new
            {
                operation = r.Operation,
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

        bool summariesWriteOk = WriteOwned(session, "serving/repetition-summaries.json", JsonOf(
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

        return new ServingRunCoordinatorResult(
            PlannedExecutionCount: 15,
            AttemptedExecutionCount: attempted.Count,
            CoordinatorComplete: !halted,
            EvidenceValid: computedValid && coordinatorWriteOk && summariesWriteOk,
            Halted: halted,
            HaltAfterOperation: haltAfterOp,
            HaltAfterRepetition: haltAfterRep,
            HaltReason: haltReason,
            Executions: records,
            RepetitionSummaries: summaries,
            WatchdogSeconds: watchdog.TotalSeconds);
    }

    private static bool WriteOwned(EvidenceStagingSession session, string rel, string text)
    {
        try { session.WriteText(rel, text); return true; }
        catch { return false; }
    }

    private static string JsonOf(object value) => System.Text.Json.JsonSerializer.Serialize(value, value.GetType(),
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    private static ServingExecutionRecord ToRecord(ServingChildEvidenceResult child) => new(
        child.Operation,
        child.Repetition,
        child.EvidenceValid,
        child.RegisteredStableArtifacts,
        child.ProcessOutcome.ToString(),
        child.ResourceStatus.ToString(),
        child.Envelope?.Status.ToString(),
        child.MeasuredSequenceComplete,
        child.ParentSamples.Count);

    private static ServingChildSnapshot ToSnapshot(ServingChildEvidenceResult child) => new(
        child.EvidenceValid,
        child.RegisteredStableArtifacts,
        child.ProcessOutcome == ProcessOutcome.CompletedProtocolResult,
        child.Envelope?.CorrectnessStatus ?? "",
        child.MeasuredSequenceComplete,
        child.ParentSamples);

    private static Dictionary<string, List<(string, long)>> ExpectedStrata(ServingWorkload workload)
    {
        var result = new Dictionary<string, List<(string, long)>>();
        foreach (var op in Operations)
        {
            var byStratum = workload.Probes.Where(p => p.Op == op && p.Measured)
                .GroupBy(p => p.Stratum)
                .ToDictionary(g => g.Key, g => g.LongCount());
            if (byStratum.Count == 0) continue;
            result[op] = byStratum.Select(kv => (kv.Key, kv.Value)).ToList();
        }
        return result;
    }
}

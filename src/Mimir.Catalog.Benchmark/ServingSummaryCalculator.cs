using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Computes one (operation,stratum,repetition) repetition summary from the
/// closed one-child facts. Parent-samples are the only measurement source;
/// no re-parsing, re-classification or re-validation happens here.
///
/// Diagnostic trust (may the parent-validated samples feed counts?) is separate
/// from summary completeness. A trustworthy timed-ERROR prefix keeps its
/// diagnostic counts even though MeasuredSequenceComplete is false; it never
/// yields metrics.
/// </summary>
public static class ServingSummaryCalculator
{
    public static ServingRepetitionSummary Summarize(
        string operation,
        string stratum,
        int repetition,
        long expectedCount,
        ServingChildSnapshot? snapshot)
    {
        if (snapshot is null)
            return Incomplete(operation, stratum, repetition, expectedCount, new[] { ServingIncompleteReason.NotAttemptedDueToHalt });

        var reasons = new List<ServingIncompleteReason>();

        if (!snapshot.EvidenceValid) reasons.Add(ServingIncompleteReason.ExecutionEvidenceInvalid);
        if (!snapshot.RegisteredStableArtifacts) reasons.Add(ServingIncompleteReason.StableArtifactCaptureFailed);
        if (!snapshot.ProcessCompleted) reasons.Add(ServingIncompleteReason.ProcessIncomplete);
        if (snapshot.EnvelopeStatus != "VALID") reasons.Add(ServingIncompleteReason.EnvelopeNotValid);
        if (!snapshot.MeasuredSequenceComplete) reasons.Add(ServingIncompleteReason.MeasuredSequenceIncomplete);

        // Parent samples are authoritative diagnostics whenever evidence is valid,
        // stable capture succeeded and the process completed. MeasuredSequenceComplete
        // gates metrics/validity only, not diagnostic counts.
        bool diagnosticTrust = snapshot.EvidenceValid && snapshot.RegisteredStableArtifacts && snapshot.ProcessCompleted;
        var samples = diagnosticTrust
            ? snapshot.Samples.Where(s => s.Stratum == stratum).OrderBy(s => s.Sequence).ToList()
            : new List<ServingParentSample>();

        if (diagnosticTrust)
        {
            if (samples.Any(s => s.Status == TimedResultStatus.Invalid)) reasons.Add(ServingIncompleteReason.InvalidSample);
            if (samples.Any(s => s.Status == TimedResultStatus.Timeout)) reasons.Add(ServingIncompleteReason.TimeoutSample);
            if (samples.Any(s => s.Status == TimedResultStatus.Error)) reasons.Add(ServingIncompleteReason.ErrorSample);
            if (samples.Count != expectedCount) reasons.Add(ServingIncompleteReason.MissingSamples);
        }

        long observed = diagnosticTrust ? samples.Count : 0;
        long invalid = diagnosticTrust ? samples.Count(s => s.Status == TimedResultStatus.Invalid) : 0;
        long timeout = diagnosticTrust ? samples.Count(s => s.Status == TimedResultStatus.Timeout) : 0;
        long error = diagnosticTrust ? samples.Count(s => s.Status == TimedResultStatus.Error) : 0;
        long valid = diagnosticTrust ? samples.Count(s => s.Status == TimedResultStatus.Valid) : 0;

        // Mechanically deterministic reason ordering.
        var orderedReasons = reasons.Distinct().OrderBy(r => r).ToList();

        ServingSummaryMetrics? metrics = null;
        if (orderedReasons.Count == 0 && observed == expectedCount && observed > 0 && valid == observed)
        {
            var wall = samples.Select(s => s.WallSeconds).OrderBy(v => v).ToList();
            double sum = wall.Sum();
            metrics = new ServingSummaryMetrics(
                observed,
                wall[0],
                WorkloadMetrics.Percentile(wall, 0.50),
                WorkloadMetrics.Percentile(wall, 0.90),
                WorkloadMetrics.Percentile(wall, 0.95),
                WorkloadMetrics.Percentile(wall, 0.99),
                wall[^1],
                sum / observed,
                WorkloadMetrics.ThroughputPerSecond(observed, sum));
        }

        var status = orderedReasons.Count == 0 ? ServingSummaryStatus.Valid : ServingSummaryStatus.Incomplete;
        return new ServingRepetitionSummary(operation, stratum, repetition, status, orderedReasons,
            expectedCount, observed, valid, invalid, timeout, error, metrics);
    }

    private static ServingRepetitionSummary Incomplete(
        string operation, string stratum, int repetition, long expectedCount, IReadOnlyList<ServingIncompleteReason> reasons)
        => new(operation, stratum, repetition, ServingSummaryStatus.Incomplete, reasons,
            expectedCount, 0, 0, 0, 0, 0, null);
}

using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Computes one (operation,stratum,repetition) repetition summary from the
/// closed one-child facts. Parent-samples are the only measurement source;
/// no re-parsing, re-classification or re-validation happens here.
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
        bool trustworthy = snapshot.EvidenceValid && snapshot.RegisteredStableArtifacts;

        if (!snapshot.EvidenceValid) reasons.Add(ServingIncompleteReason.ExecutionEvidenceInvalid);
        if (!snapshot.RegisteredStableArtifacts) reasons.Add(ServingIncompleteReason.StableArtifactCaptureFailed);
        if (!snapshot.ProcessCompleted) reasons.Add(ServingIncompleteReason.ProcessIncomplete);
        if (snapshot.EnvelopeStatus != "VALID") reasons.Add(ServingIncompleteReason.EnvelopeNotValid);
        if (!snapshot.MeasuredSequenceComplete) reasons.Add(ServingIncompleteReason.MeasuredSequenceIncomplete);

        bool samplesTrustworthy = trustworthy && snapshot.ProcessCompleted && snapshot.MeasuredSequenceComplete;
        var samples = samplesTrustworthy
            ? snapshot.Samples.Where(s => s.Stratum == stratum).OrderBy(s => s.Sequence).ToList()
            : new List<ServingParentSample>();

        if (samplesTrustworthy)
        {
            if (samples.Any(s => s.Status == TimedResultStatus.Invalid)) reasons.Add(ServingIncompleteReason.InvalidSample);
            if (samples.Any(s => s.Status == TimedResultStatus.Timeout)) reasons.Add(ServingIncompleteReason.TimeoutSample);
            if (samples.Any(s => s.Status == TimedResultStatus.Error)) reasons.Add(ServingIncompleteReason.ErrorSample);
            if (samples.Count != expectedCount) reasons.Add(ServingIncompleteReason.MissingSamples);
        }
        // Envelope-level Tail INVALID/ERROR with complete all-valid measured
        // samples carries no per-sample reason; EnvelopeNotValid above suffices.

        long observed = samplesTrustworthy ? samples.Count : 0;
        long invalid = samplesTrustworthy ? samples.Count(s => s.Status == TimedResultStatus.Invalid) : 0;
        long timeout = samplesTrustworthy ? samples.Count(s => s.Status == TimedResultStatus.Timeout) : 0;
        long error = samplesTrustworthy ? samples.Count(s => s.Status == TimedResultStatus.Error) : 0;
        long valid = samplesTrustworthy ? samples.Count(s => s.Status == TimedResultStatus.Valid) : 0;

        ServingSummaryMetrics? metrics = null;
        if (reasons.Count == 0 && observed == expectedCount && observed > 0 && valid == observed)
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

        var status = reasons.Count == 0 ? ServingSummaryStatus.Valid : ServingSummaryStatus.Incomplete;
        return new ServingRepetitionSummary(operation, stratum, repetition, status, reasons,
            expectedCount, observed, valid, invalid, timeout, error, metrics);
    }

    private static ServingRepetitionSummary Incomplete(
        string operation, string stratum, int repetition, long expectedCount, IReadOnlyList<ServingIncompleteReason> reasons)
        => new(operation, stratum, repetition, ServingSummaryStatus.Incomplete, reasons,
            expectedCount, 0, 0, 0, 0, 0, null);
}

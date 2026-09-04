using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Computes one (G1,stratum,repetition) summary from the closed one-child facts.
/// Parent samples are the only measurement source; no re-parsing,
/// re-classification or re-validation happens here. Diagnostic trust is separate
/// from summary validity: a trustworthy INVALID/TIMEOUT/ERROR prefix keeps its
/// diagnostic counts but never yields metrics.
/// </summary>
public static class G1SummaryCalculator
{
    public const string Operation = "G1";

    public static G1RepetitionSummary Summarize(
        string stratum,
        int repetition,
        long expectedCount,
        G1ChildSnapshot? snapshot)
    {
        if (snapshot is null)
            return Incomplete(stratum, repetition, expectedCount, new[] { G1IncompleteReason.NotAttemptedDueToHalt });

        var reasons = new List<G1IncompleteReason>();
        if (!snapshot.EvidenceValid) reasons.Add(G1IncompleteReason.ExecutionEvidenceInvalid);
        if (!snapshot.RegisteredStableArtifacts) reasons.Add(G1IncompleteReason.StableArtifactCaptureFailed);
        if (!snapshot.ProcessCompleted) reasons.Add(G1IncompleteReason.ProcessIncomplete);
        if (snapshot.EnvelopeStatus != "VALID") reasons.Add(G1IncompleteReason.EnvelopeNotValid);
        if (!snapshot.MeasuredSequenceComplete) reasons.Add(G1IncompleteReason.MeasuredSequenceIncomplete);

        bool diagnosticTrust = snapshot.EvidenceValid && snapshot.RegisteredStableArtifacts && snapshot.ProcessCompleted;
        var samples = diagnosticTrust
            ? snapshot.ParentSamples.Where(s => s.Stratum == stratum).OrderBy(s => s.Sequence).ToList()
            : new List<G1ParentSample>();

        if (diagnosticTrust)
        {
            if (samples.Count != expectedCount) reasons.Add(G1IncompleteReason.MissingSamples);
            if (samples.Any(s => s.Status == TimedResultStatus.Invalid)) reasons.Add(G1IncompleteReason.InvalidSample);
            if (samples.Any(s => s.Status == TimedResultStatus.Timeout)) reasons.Add(G1IncompleteReason.TimeoutSample);
            if (samples.Any(s => s.Status == TimedResultStatus.Error)) reasons.Add(G1IncompleteReason.ErrorSample);
        }

        long observed = diagnosticTrust ? samples.Count : 0;
        long invalid = diagnosticTrust ? samples.Count(s => s.Status == TimedResultStatus.Invalid) : 0;
        long timeout = diagnosticTrust ? samples.Count(s => s.Status == TimedResultStatus.Timeout) : 0;
        long error = diagnosticTrust ? samples.Count(s => s.Status == TimedResultStatus.Error) : 0;
        long valid = diagnosticTrust ? samples.Count(s => s.Status == TimedResultStatus.Valid) : 0;

        var orderedReasons = reasons.Distinct().OrderBy(r => r).ToList();

        G1SummaryMetrics? metrics = null;
        if (orderedReasons.Count == 0 && observed == expectedCount && observed > 0 && valid == observed)
        {
            var wall = samples.Select(s => s.WallSeconds).OrderBy(v => v).ToList();
            double sum = wall.Sum();
            metrics = new G1SummaryMetrics(
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

        var status = orderedReasons.Count == 0 ? G1SummaryStatus.Valid : G1SummaryStatus.Incomplete;
        return new G1RepetitionSummary(Operation, stratum, repetition, status, orderedReasons,
            expectedCount, observed, valid, invalid, timeout, error, metrics);
    }

    private static G1RepetitionSummary Incomplete(
        string stratum, int repetition, long expectedCount, IReadOnlyList<G1IncompleteReason> reasons)
        => new(Operation, stratum, repetition, G1SummaryStatus.Incomplete, reasons,
            expectedCount, 0, 0, 0, 0, 0, null);
}

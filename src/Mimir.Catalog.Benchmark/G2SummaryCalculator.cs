namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Computes one (G2,repetition) summary from the closed one-child facts. One
/// measured Batch per repetition; no within-repetition percentiles/throughput.
/// Structural reasons are derived independent of diagnostic trust; Batch
/// presence/status diagnostics require diagnosticTrust. BatchWallSeconds is only
/// ever set for a Valid summary.
/// </summary>
public static class G2SummaryCalculator
{
    public const string Operation = "G2";

    public static G2RepetitionSummary Summarize(
        int repetition,
        int expectedPerInputCount,
        G2ChildSnapshot? snapshot)
    {
        if (snapshot is null)
            return Incomplete(repetition, expectedPerInputCount, new[] { G2IncompleteReason.NotAttemptedDueToHalt });

        var reasons = new List<G2IncompleteReason>();
        if (!snapshot.EvidenceValid) reasons.Add(G2IncompleteReason.ExecutionEvidenceInvalid);
        if (!snapshot.RegisteredStableArtifacts) reasons.Add(G2IncompleteReason.StableArtifactCaptureFailed);
        if (!snapshot.ProcessCompleted) reasons.Add(G2IncompleteReason.ProcessIncomplete);
        if (snapshot.EnvelopeStatus != "VALID") reasons.Add(G2IncompleteReason.EnvelopeNotValid);
        if (!snapshot.TimedBatchComplete) reasons.Add(G2IncompleteReason.TimedBatchIncomplete);

        bool diagnosticTrust = snapshot.EvidenceValid && snapshot.RegisteredStableArtifacts && snapshot.ProcessCompleted;
        int observedPerInput = diagnosticTrust ? snapshot.ObservedPerInputCount : 0;
        G2ParentBatch? batch = diagnosticTrust ? snapshot.Batch : null;

        if (diagnosticTrust && batch is null)
            reasons.Add(G2IncompleteReason.MissingBatch);

        if (batch is not null)
        {
            if (batch.Status == TimedResultStatus.Invalid) reasons.Add(G2IncompleteReason.InvalidBatch);
            if (batch.Status == TimedResultStatus.Timeout) reasons.Add(G2IncompleteReason.TimeoutBatch);
            if (batch.Status == TimedResultStatus.Error) reasons.Add(G2IncompleteReason.ErrorBatch);
        }

        var ordered = reasons.Distinct().OrderBy(r => r).ToList();
        bool valid = ordered.Count == 0 && snapshot.EnvelopeStatus == "VALID" && snapshot.TimedBatchComplete
            && batch is not null && batch.Status == TimedResultStatus.Valid;

        if (valid)
        {
            return new G2RepetitionSummary(Operation, repetition, G2SummaryStatus.Valid, ordered,
                expectedPerInputCount, observedPerInput,
                batch!.ChildCorrectness, batch.Status,
                batch.WallSeconds, batch.WallSeconds);
        }

        return new G2RepetitionSummary(Operation, repetition, G2SummaryStatus.Incomplete, ordered,
            expectedPerInputCount, observedPerInput,
            diagnosticTrust && batch is not null ? batch.ChildCorrectness : null,
            diagnosticTrust && batch is not null ? batch.Status : null,
            null,
            diagnosticTrust && batch is not null ? batch.WallSeconds : null);
    }

    private static G2RepetitionSummary Incomplete(int repetition, int expectedPerInputCount, IReadOnlyList<G2IncompleteReason> reasons)
        => new(Operation, repetition, G2SummaryStatus.Incomplete, reasons,
            expectedPerInputCount, 0, null, null, null, null);
}

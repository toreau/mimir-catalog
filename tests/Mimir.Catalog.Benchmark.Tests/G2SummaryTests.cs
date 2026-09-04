using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class G2SummaryTests
{
    private static G2ParentBatch Batch(TimedResultStatus status, double wall = 10.0, string? child = null)
        => new(wall, status, child ?? (status switch
        {
            TimedResultStatus.Invalid => "INVALID",
            TimedResultStatus.Error => "ERROR",
            _ => "VALID",
        }));

    private static G2ChildSnapshot Snapshot(
        bool evidenceValid = true,
        bool stable = true,
        bool processCompleted = true,
        string envelopeStatus = "VALID",
        bool timedBatchComplete = true,
        G2ParentBatch? batch = null,
        int observedPerInput = 200)
        => new(evidenceValid, stable, processCompleted, envelopeStatus, timedBatchComplete, batch, observedPerInput);

    [Fact]
    public void Valid_Summary_WallsAndFacts()
    {
        var summary = G2SummaryCalculator.Summarize(1, 200, Snapshot(batch: Batch(TimedResultStatus.Valid)));
        Assert.Equal(G2SummaryStatus.Valid, summary.Status);
        Assert.Empty(summary.Reasons);
        Assert.Equal(200, summary.ObservedPerInputCount);
        Assert.Equal("VALID", summary.ChildCorrectness);
        Assert.Equal(TimedResultStatus.Valid, summary.TimedStatus);
        Assert.Equal(10.0, summary.BatchWallSeconds);
        Assert.Equal(10.0, summary.ObservedDiagnosticWallSeconds);
    }

    [Fact]
    public void UntrustedSnapshot_HidesDiagnostics_NoMissingBatchReason()
    {
        var summary = G2SummaryCalculator.Summarize(1, 200,
            Snapshot(evidenceValid: false, batch: Batch(TimedResultStatus.Valid)));
        Assert.Equal(G2SummaryStatus.Incomplete, summary.Status);
        Assert.Contains(G2IncompleteReason.ExecutionEvidenceInvalid, summary.Reasons);
        Assert.DoesNotContain(G2IncompleteReason.MissingBatch, summary.Reasons);
        Assert.Equal(0, summary.ObservedPerInputCount);
        Assert.Null(summary.ChildCorrectness);
        Assert.Null(summary.TimedStatus);
        Assert.Null(summary.ObservedDiagnosticWallSeconds);
        Assert.Null(summary.BatchWallSeconds);
    }

    [Fact]
    public void StructuralTimedBatchIncomplete_IndependentOfTrust()
    {
        var summary = G2SummaryCalculator.Summarize(1, 200,
            Snapshot(evidenceValid: false, timedBatchComplete: false, batch: Batch(TimedResultStatus.Valid)));
        Assert.Contains(G2IncompleteReason.TimedBatchIncomplete, summary.Reasons);
        Assert.DoesNotContain(G2IncompleteReason.MissingBatch, summary.Reasons); // untrusted: no batch diagnostics
    }

    [Fact]
    public void TrustworthyZeroBatch_EmitsBothReasons()
    {
        var summary = G2SummaryCalculator.Summarize(1, 200, Snapshot(timedBatchComplete: false, batch: null));
        Assert.Equal(G2SummaryStatus.Incomplete, summary.Status);
        Assert.Contains(G2IncompleteReason.TimedBatchIncomplete, summary.Reasons);
        Assert.Contains(G2IncompleteReason.MissingBatch, summary.Reasons);
        Assert.Null(summary.BatchWallSeconds);
        Assert.Null(summary.ObservedDiagnosticWallSeconds);
        Assert.Null(summary.TimedStatus);
        Assert.Null(summary.ChildCorrectness);
    }

    [Fact]
    public void Timeout_RetainsDiagnosticWall_NoAuthoritativeWall()
    {
        var summary = G2SummaryCalculator.Summarize(1, 200, Snapshot(batch: Batch(TimedResultStatus.Timeout, wall: 130.0)));
        Assert.Equal(G2SummaryStatus.Incomplete, summary.Status);
        Assert.Contains(G2IncompleteReason.TimeoutBatch, summary.Reasons);
        Assert.Null(summary.BatchWallSeconds);
        Assert.Equal(130.0, summary.ObservedDiagnosticWallSeconds);
        Assert.Equal(TimedResultStatus.Timeout, summary.TimedStatus);
    }

    [Fact]
    public void Invalid_RetainsDiagnosticWall_NoAuthoritativeWall()
    {
        var summary = G2SummaryCalculator.Summarize(1, 200,
            Snapshot(envelopeStatus: "INVALID", batch: Batch(TimedResultStatus.Invalid, wall: 5.0)));
        Assert.Equal(G2SummaryStatus.Incomplete, summary.Status);
        Assert.Contains(G2IncompleteReason.EnvelopeNotValid, summary.Reasons);
        Assert.Contains(G2IncompleteReason.InvalidBatch, summary.Reasons);
        Assert.Null(summary.BatchWallSeconds);
        Assert.Equal(5.0, summary.ObservedDiagnosticWallSeconds);
    }

    [Fact]
    public void TimedBatchError_Complete_TimedBatchCompleteTrue_RetainsWall()
    {
        var summary = G2SummaryCalculator.Summarize(1, 200,
            Snapshot(envelopeStatus: "ERROR", timedBatchComplete: true, batch: Batch(TimedResultStatus.Error, wall: 7.0)));
        Assert.Equal(G2SummaryStatus.Incomplete, summary.Status);
        Assert.Contains(G2IncompleteReason.EnvelopeNotValid, summary.Reasons);
        Assert.Contains(G2IncompleteReason.ErrorBatch, summary.Reasons);
        Assert.DoesNotContain(G2IncompleteReason.TimedBatchIncomplete, summary.Reasons); // complete timed ERROR
        Assert.DoesNotContain(G2IncompleteReason.MissingBatch, summary.Reasons);
        Assert.Null(summary.BatchWallSeconds);
        Assert.Equal(7.0, summary.ObservedDiagnosticWallSeconds);
    }

    [Fact]
    public void ZeroBatchInvalid_EnvelopeReasonToo()
    {
        var summary = G2SummaryCalculator.Summarize(1, 200,
            Snapshot(envelopeStatus: "INVALID", timedBatchComplete: false, batch: null));
        Assert.Contains(G2IncompleteReason.EnvelopeNotValid, summary.Reasons);
        Assert.Contains(G2IncompleteReason.TimedBatchIncomplete, summary.Reasons);
        Assert.Contains(G2IncompleteReason.MissingBatch, summary.Reasons);
    }

    [Fact]
    public void NotAttempted_NullSnapshot()
    {
        var summary = G2SummaryCalculator.Summarize(3, 200, null);
        Assert.Equal(G2SummaryStatus.Incomplete, summary.Status);
        Assert.Equal(new[] { G2IncompleteReason.NotAttemptedDueToHalt }, summary.Reasons);
        Assert.Equal(0, summary.ObservedPerInputCount);
        Assert.Null(summary.ChildCorrectness);
        Assert.Null(summary.TimedStatus);
        Assert.Null(summary.BatchWallSeconds);
        Assert.Null(summary.ObservedDiagnosticWallSeconds);
    }

    [Fact]
    public void ValidBatchRequiresValidEnvelope_AndTimedComplete()
    {
        var badEnvelope = G2SummaryCalculator.Summarize(1, 200,
            Snapshot(envelopeStatus: "INVALID", batch: Batch(TimedResultStatus.Valid)));
        Assert.NotEqual(G2SummaryStatus.Valid, badEnvelope.Status);

        var notComplete = G2SummaryCalculator.Summarize(1, 200,
            Snapshot(timedBatchComplete: false, batch: Batch(TimedResultStatus.Valid)));
        Assert.NotEqual(G2SummaryStatus.Valid, notComplete.Status);

        var missing = G2SummaryCalculator.Summarize(1, 200, Snapshot(batch: null));
        Assert.NotEqual(G2SummaryStatus.Valid, missing.Status);
    }

    [Fact]
    public void ReasonOrdering_Deterministic()
    {
        var summary = G2SummaryCalculator.Summarize(1, 200,
            Snapshot(envelopeStatus: "INVALID", timedBatchComplete: true, batch: Batch(TimedResultStatus.Error)));
        Assert.Equal(new[] { G2IncompleteReason.EnvelopeNotValid, G2IncompleteReason.ErrorBatch }, summary.Reasons);
    }
}

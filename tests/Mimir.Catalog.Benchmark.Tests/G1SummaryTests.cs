using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class G1SummaryTests
{
    private static G1ParentSample Sample(long seq, string stratum, TimedResultStatus status, double wall = 0.5)
        => new("G1", seq, stratum, wall, status, status switch
        {
            TimedResultStatus.Invalid => "INVALID",
            TimedResultStatus.Error => "ERROR",
            _ => "VALID",
        });

    private static G1ChildSnapshot Snapshot(
        bool evidenceValid = true,
        bool stable = true,
        bool processCompleted = true,
        string envelopeStatus = "VALID",
        bool measuredComplete = true,
        IReadOnlyList<G1ParentSample>? samples = null)
        => new(evidenceValid, stable, processCompleted, envelopeStatus, measuredComplete, samples ?? Array.Empty<G1ParentSample>());

    [Fact]
    public void Valid_ExactMetrics()
    {
        var samples = new List<G1ParentSample>
        {
            Sample(0, "Degree1", TimedResultStatus.Valid, wall: 3.0),
            Sample(1, "Degree1", TimedResultStatus.Valid, wall: 1.0),
            Sample(2, "Degree1", TimedResultStatus.Valid, wall: 2.0),
        };
        var summary = G1SummaryCalculator.Summarize("Degree1", 1, expectedCount: 3, Snapshot(samples: samples));
        Assert.Equal(G1SummaryStatus.Valid, summary.Status);
        Assert.Empty(summary.Reasons);
        Assert.NotNull(summary.Metrics);
        Assert.Equal(3, summary.Metrics!.Count);
        Assert.Equal(1.0, summary.Metrics.MinSeconds);
        Assert.Equal(3.0, summary.Metrics.MaxSeconds);
        Assert.Equal(2.0, summary.Metrics.MeanSeconds);
        Assert.Equal(0.5, summary.Metrics.ThroughputPerSecond); // 3 / 6
    }

    [Fact]
    public void EnvelopeNotValid_Incomplete_NoMetrics()
    {
        var samples = new List<G1ParentSample> { Sample(0, "Degree1", TimedResultStatus.Valid) };
        var summary = G1SummaryCalculator.Summarize("Degree1", 1, 1, Snapshot(envelopeStatus: "INVALID", samples: samples));
        Assert.Equal(G1SummaryStatus.Incomplete, summary.Status);
        Assert.Equal(new[] { G1IncompleteReason.EnvelopeNotValid }, summary.Reasons);
        Assert.Null(summary.Metrics);
    }

    [Theory]
    [InlineData(TimedResultStatus.Timeout, G1IncompleteReason.TimeoutSample)]
    [InlineData(TimedResultStatus.Invalid, G1IncompleteReason.InvalidSample)]
    [InlineData(TimedResultStatus.Error, G1IncompleteReason.ErrorSample)]
    public void PointFailures_Incomplete_NoMetrics(TimedResultStatus status, G1IncompleteReason reason)
    {
        var samples = new List<G1ParentSample> { Sample(0, "Degree1", status) };
        var summary = G1SummaryCalculator.Summarize("Degree1", 1, 1, Snapshot(samples: samples));
        Assert.Equal(G1SummaryStatus.Incomplete, summary.Status);
        Assert.Contains(reason, summary.Reasons);
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public void NotAttempted_NullSnapshot()
    {
        var summary = G1SummaryCalculator.Summarize("Degree1", 3, 1, null);
        Assert.Equal(G1SummaryStatus.Incomplete, summary.Status);
        Assert.Equal(new[] { G1IncompleteReason.NotAttemptedDueToHalt }, summary.Reasons);
        Assert.All(new[] { summary.ObservedCount, summary.ValidCount, summary.InvalidCount, summary.TimeoutCount, summary.ErrorCount }, v => Assert.Equal(0, v));
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public void UntrustedChild_ZeroCounts_EvidenceReason()
    {
        var summary = G1SummaryCalculator.Summarize("Degree1", 1, 1,
            Snapshot(evidenceValid: false, samples: new[] { Sample(0, "Degree1", TimedResultStatus.Valid) }));
        Assert.Equal(G1SummaryStatus.Incomplete, summary.Status);
        Assert.Contains(G1IncompleteReason.ExecutionEvidenceInvalid, summary.Reasons);
        Assert.Equal(0, summary.ObservedCount);
        Assert.Equal(0, summary.ValidCount);
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public void MissingSamples_Mismatch()
    {
        var samples = new List<G1ParentSample> { Sample(0, "Degree1", TimedResultStatus.Valid) };
        var summary = G1SummaryCalculator.Summarize("Degree1", 1, 2, Snapshot(samples: samples));
        Assert.Contains(G1IncompleteReason.MissingSamples, summary.Reasons);
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public void MeasuredSequenceIncomplete_Reason()
    {
        var samples = new List<G1ParentSample> { Sample(0, "Degree1", TimedResultStatus.Valid) };
        var summary = G1SummaryCalculator.Summarize("Degree1", 1, 1, Snapshot(measuredComplete: false, samples: samples));
        Assert.Contains(G1IncompleteReason.MeasuredSequenceIncomplete, summary.Reasons);
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public void TrustworthyErrorPrefix_CountsRetained_NoMetrics()
    {
        var samples = new List<G1ParentSample>
        {
            Sample(0, "Degree1", TimedResultStatus.Valid, wall: 1.0),
            Sample(1, "Degree1", TimedResultStatus.Invalid),
            Sample(2, "Degree1", TimedResultStatus.Error),
        };
        var summary = G1SummaryCalculator.Summarize("Degree1", 1, 5, Snapshot(samples: samples));
        Assert.Equal(G1SummaryStatus.Incomplete, summary.Status);
        Assert.Null(summary.Metrics);
        Assert.Equal(3, summary.ObservedCount);
        Assert.Equal(1, summary.ValidCount);
        Assert.Equal(1, summary.InvalidCount);
        Assert.Equal(1, summary.ErrorCount);
        Assert.Contains(G1IncompleteReason.MissingSamples, summary.Reasons);
        Assert.Contains(G1IncompleteReason.ErrorSample, summary.Reasons);
        Assert.Contains(G1IncompleteReason.InvalidSample, summary.Reasons);
    }

    [Fact]
    public void NoStratumPooling()
    {
        var samples = new List<G1ParentSample>
        {
            Sample(0, "Degree1", TimedResultStatus.Valid, wall: 2.0),
            Sample(0, "Degree2Plus", TimedResultStatus.Error),
        };
        var summary = G1SummaryCalculator.Summarize("Degree1", 1, 1, Snapshot(samples: samples));
        Assert.Equal(G1SummaryStatus.Valid, summary.Status);
        Assert.Equal(1, summary.ObservedCount);
        Assert.Equal(2.0, summary.Metrics!.MeanSeconds);
    }
}

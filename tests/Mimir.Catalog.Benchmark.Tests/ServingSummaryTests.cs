using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class ServingSummaryTests
{
    private static ServingParentSample Sample(string stratum, long seq, TimedResultStatus status, double wall = 0.5)
        => new("S1", seq, stratum, wall, status, status switch
        {
            TimedResultStatus.Invalid => "INVALID",
            TimedResultStatus.Error => "ERROR",
            _ => "VALID",
        });

    private static ServingChildSnapshot Snapshot(
        bool evidenceValid = true,
        bool stable = true,
        bool processCompleted = true,
        string envelopeStatus = "VALID",
        bool measuredComplete = true,
        IReadOnlyList<ServingParentSample>? samples = null)
        => new(evidenceValid, stable, processCompleted, envelopeStatus, measuredComplete, samples ?? Array.Empty<ServingParentSample>());

    [Fact]
    public void Valid_ExactMetrics_AndCounts()
    {
        var samples = new List<ServingParentSample>
        {
            Sample("Hit", 1, TimedResultStatus.Valid, wall: 3.0),
            Sample("Hit", 2, TimedResultStatus.Valid, wall: 1.0),
            Sample("Hit", 3, TimedResultStatus.Valid, wall: 2.0),
        };
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, expectedCount: 3, Snapshot(samples: samples));
        Assert.Equal(ServingSummaryStatus.Valid, summary.Status);
        Assert.Empty(summary.Reasons);
        Assert.NotNull(summary.Metrics);
        Assert.Equal(3, summary.ObservedCount);
        Assert.Equal(3, summary.ValidCount);
        var metrics = summary.Metrics!;
        Assert.Equal(1.0, metrics.MinSeconds);
        Assert.Equal(3.0, metrics.MaxSeconds);
        Assert.Equal(2.0, metrics.MeanSeconds);
        Assert.Equal(2.0, metrics.P50Seconds);
        Assert.Equal(2.98, metrics.P99Seconds);
        Assert.Equal(0.5, metrics.ThroughputPerSecond); // 3 / (3+1+2)
    }

    [Fact]
    public void EnvelopeNotValid_Incomplete_NoMetrics()
    {
        var samples = new List<ServingParentSample> { Sample("Hit", 1, TimedResultStatus.Valid) };
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, 1, Snapshot(envelopeStatus: "INVALID", samples: samples));
        Assert.Equal(ServingSummaryStatus.Incomplete, summary.Status);
        Assert.Equal(new[] { ServingIncompleteReason.EnvelopeNotValid }, summary.Reasons);
        Assert.Null(summary.Metrics);
    }

    [Theory]
    [InlineData(TimedResultStatus.Timeout, ServingIncompleteReason.TimeoutSample)]
    [InlineData(TimedResultStatus.Invalid, ServingIncompleteReason.InvalidSample)]
    [InlineData(TimedResultStatus.Error, ServingIncompleteReason.ErrorSample)]
    public void PointFailures_Incomplete(TimedResultStatus status, ServingIncompleteReason expectedReason)
    {
        var samples = new List<ServingParentSample> { Sample("Hit", 1, status) };
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, 1, Snapshot(samples: samples));
        Assert.Equal(ServingSummaryStatus.Incomplete, summary.Status);
        Assert.Contains(expectedReason, summary.Reasons);
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public void NotAttempted_NullSnapshot()
    {
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, 1, null);
        Assert.Equal(ServingSummaryStatus.Incomplete, summary.Status);
        Assert.Equal(new[] { ServingIncompleteReason.NotAttemptedDueToHalt }, summary.Reasons);
        Assert.All(new[] { summary.ObservedCount, summary.ValidCount, summary.InvalidCount, summary.TimeoutCount, summary.ErrorCount }, v => Assert.Equal(0, v));
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public void UntrustworthyChild_ZeroCounts_EvidenceReason()
    {
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, 1,
            Snapshot(evidenceValid: false, samples: new[] { Sample("Hit", 1, TimedResultStatus.Valid) }));
        Assert.Contains(ServingIncompleteReason.ExecutionEvidenceInvalid, summary.Reasons);
        Assert.Equal(0, summary.ObservedCount);
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public void ProcessIncomplete_Reason()
    {
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, 1,
            Snapshot(processCompleted: false));
        Assert.Equal(new[] { ServingIncompleteReason.ProcessIncomplete }, summary.Reasons);
        Assert.Equal(0, summary.ObservedCount);
    }

    [Fact]
    public void MissingSamples_Mismatch()
    {
        var samples = new List<ServingParentSample> { Sample("Hit", 1, TimedResultStatus.Valid) };
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, 2, Snapshot(samples: samples));
        Assert.Contains(ServingIncompleteReason.MissingSamples, summary.Reasons);
        Assert.Null(summary.Metrics);
    }

    [Fact]
    public void NoStratumPooling()
    {
        // A failing sample in another stratum must not touch this stratum's summary.
        var samples = new List<ServingParentSample>
        {
            Sample("Hit", 1, TimedResultStatus.Valid, wall: 2.0),
            Sample("Miss", 1, TimedResultStatus.Error),
        };
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, 1, Snapshot(samples: samples));
        Assert.Equal(ServingSummaryStatus.Valid, summary.Status);
        Assert.Equal(1, summary.ObservedCount);
        Assert.Equal(2.0, summary.Metrics!.MeanSeconds);
    }
    [Fact]
    public void TimedErrorPrefix_CountsRetained()
    {
        // Expected 3; observed VALID,INVALID,ERROR prefix (complete length but ERROR envelope).
        var prefix = new List<ServingParentSample>
        {
            Sample("Hit", 1, TimedResultStatus.Valid, wall: 1.0),
            Sample("Hit", 2, TimedResultStatus.Invalid),
            Sample("Hit", 3, TimedResultStatus.Error),
        };
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, 3,
            Snapshot(envelopeStatus: "ERROR", measuredComplete: false, samples: prefix));
        Assert.Equal(ServingSummaryStatus.Incomplete, summary.Status);
        Assert.Null(summary.Metrics);
        Assert.Equal(3, summary.ObservedCount);
        Assert.Equal(1, summary.ValidCount);
        Assert.Equal(1, summary.InvalidCount);
        Assert.Equal(1, summary.ErrorCount);
        Assert.DoesNotContain(ServingIncompleteReason.MissingSamples, summary.Reasons);
        Assert.Contains(ServingIncompleteReason.EnvelopeNotValid, summary.Reasons);
        Assert.Contains(ServingIncompleteReason.MeasuredSequenceIncomplete, summary.Reasons);
        Assert.Contains(ServingIncompleteReason.ErrorSample, summary.Reasons);
        Assert.Contains(ServingIncompleteReason.InvalidSample, summary.Reasons);
    }

    [Fact]
    public void ShortTimedErrorPrefix_MissingSamplesRetained()
    {
        // Expected 5; observed VALID,ERROR prefix.
        var prefix = new List<ServingParentSample>
        {
            Sample("Hit", 1, TimedResultStatus.Valid, wall: 1.0),
            Sample("Hit", 2, TimedResultStatus.Error),
        };
        var summary = ServingSummaryCalculator.Summarize("S1", "Hit", 1, 5,
            Snapshot(envelopeStatus: "ERROR", measuredComplete: false, samples: prefix));
        Assert.Equal(ServingSummaryStatus.Incomplete, summary.Status);
        Assert.Null(summary.Metrics);
        Assert.Equal(2, summary.ObservedCount);
        Assert.Equal(1, summary.ValidCount);
        Assert.Equal(1, summary.ErrorCount);
        Assert.Contains(ServingIncompleteReason.MissingSamples, summary.Reasons);
        Assert.Contains(ServingIncompleteReason.ErrorSample, summary.Reasons);
    }
}


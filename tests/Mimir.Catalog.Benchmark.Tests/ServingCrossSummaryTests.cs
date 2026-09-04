using Mimir.Catalog.Benchmark;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark.Tests;

public class ServingCrossSummaryTests
{
    private static ServingWorkload Workload(params (string Op, string Stratum)[] measured)
    {
        var probes = new List<ServingProbe>();
        var expected = new Dictionary<(string, long), ServingExpected>();
        long seq = 1;
        foreach (var (op, stratum) in measured)
        {
            probes.Add(new ServingProbe(op, seq, stratum, true, 100 + seq, null, null));
            expected[(op, seq)] = new ServingExpected(op, seq, true, 1, "d");
            seq++;
        }
        return new ServingWorkload { Probes = probes, Expected = expected };
    }

    private static ServingWorkload WorkloadWithTail()
    {
        var probes = new List<ServingProbe>
        {
            new("S1", 1, "Hit", true, 101, null, null),
            new("S1", 900, "Tail", false, 999, null, null),
        };
        return new ServingWorkload
        {
            Probes = probes,
            Expected = new Dictionary<(string, long), ServingExpected> { [("S1", 1)] = new("S1", 1, true, 1, "d") },
        };
    }

    private static ServingSummaryMetrics Metrics(long count, double baseValue, double throughput = 1)
        => new(count, baseValue, baseValue, baseValue, baseValue, baseValue, baseValue, baseValue, throughput);

    private static ServingRepetitionSummary ValidRep(string op, string stratum, int rep, long expected, double baseValue)
        => new(op, stratum, rep, ServingSummaryStatus.Valid, Array.Empty<ServingIncompleteReason>(),
            expected, expected, expected, 0, 0, 0, Metrics(expected, baseValue));

    private static ServingRepetitionSummary IncompleteRep(string op, string stratum, int rep, long expected, params ServingIncompleteReason[] reasons)
        => new(op, stratum, rep, ServingSummaryStatus.Incomplete, reasons, expected, 0, 0, 0, 0, 0, null);

    private static ServingRepetitionSummary Raw(
        string op, string stratum, int rep, ServingSummaryStatus status,
        long expected, IReadOnlyList<ServingIncompleteReason> reasons,
        long observed, long valid, long invalid, long timeout, long error, ServingSummaryMetrics? metrics)
        => new(op, stratum, rep, status, reasons, expected, observed, valid, invalid, timeout, error, metrics);

    private static ServingCrossCalculationResult Calc(ServingWorkload workload, params ServingRepetitionSummary[] summaries)
        => ServingCrossSummaryCalculator.Calculate(workload, summaries);

    [Fact]
    public void ThreeValid_CrossValid_ExactMedians()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            ValidRep("S1", "Hit", 1, 1, 10),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.True(result.InputIntegrityValid);
        Assert.True(result.ServingComparisonReady);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(ServingSummaryStatus.Valid, cross.Status);
        Assert.Equal(1, cross.ExpectedCount);
        Assert.Equal(3, cross.ValidRepetitionCount);
        var m = cross.Metrics!;
        Assert.Equal(1, m.Count);
        Assert.Equal(20, m.MinSeconds);
        Assert.Equal(20, m.P50Seconds);
        Assert.Equal(20, m.MaxSeconds);
        Assert.Equal(20, m.MeanSeconds);
    }

    [Fact]
    public void ShuffledInput_SameDeterministicResult()
    {
        var workload = Workload(("S1", "Hit"));
        var ordered = Calc(workload,
            ValidRep("S1", "Hit", 1, 1, 10),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30));
        var shuffled = Calc(workload,
            ValidRep("S1", "Hit", 3, 1, 30),
            ValidRep("S1", "Hit", 1, 1, 10),
            ValidRep("S1", "Hit", 2, 1, 20));
        Assert.Equal(ordered.CrossSummaries[0].Metrics!.MinSeconds, shuffled.CrossSummaries[0].Metrics!.MinSeconds);
        Assert.Equal(ordered.IntegrityProblems.Count, shuffled.IntegrityProblems.Count);
    }

    [Fact]
    public void LegitimateIncomplete_CrossIncomplete_NoSurvivorMedian()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            ValidRep("S1", "Hit", 1, 1, 10),
            IncompleteRep("S1", "Hit", 2, 1, ServingIncompleteReason.TimeoutSample),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.True(result.InputIntegrityValid);
        Assert.False(result.ServingComparisonReady);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(ServingSummaryStatus.Incomplete, cross.Status);
        Assert.Null(cross.Metrics);
        Assert.Equal(2, cross.ValidRepetitionCount);
        Assert.Equal(new[] { 2 }, cross.IncompleteRepetitions);
    }

    [Fact]
    public void AllIncomplete_CrossIncomplete()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            IncompleteRep("S1", "Hit", 1, 1, ServingIncompleteReason.EnvelopeNotValid),
            IncompleteRep("S1", "Hit", 2, 1, ServingIncompleteReason.EnvelopeNotValid),
            IncompleteRep("S1", "Hit", 3, 1, ServingIncompleteReason.EnvelopeNotValid));
        Assert.True(result.InputIntegrityValid);
        Assert.False(result.ServingComparisonReady);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(ServingSummaryStatus.Incomplete, cross.Status);
        Assert.Equal(0, cross.ValidRepetitionCount);
    }

    [Fact]
    public void MissingRep2_IntegrityInvalid()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            ValidRep("S1", "Hit", 1, 1, 10),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.False(result.InputIntegrityValid);
        Assert.False(result.ServingComparisonReady);
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.MissingRepetitionSummary && p.Repetition == 2);
    }

    [Fact]
    public void DuplicateRep2_IntegrityInvalid()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            ValidRep("S1", "Hit", 1, 1, 10),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 2, 1, 21),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.False(result.InputIntegrityValid);
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.DuplicateRepetitionSummary && p.Repetition == 2);
        Assert.Equal(ServingSummaryStatus.Incomplete, Assert.Single(result.CrossSummaries).Status);
    }

    [Fact]
    public void ExtraRep4_DoesNotCorruptExpectedMedian()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            ValidRep("S1", "Hit", 1, 1, 10),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30),
            ValidRep("S1", "Hit", 4, 1, 999));
        Assert.False(result.InputIntegrityValid); // unexpected rep-4 integrity problem
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.UnexpectedRepetitionNumber && p.Repetition == 4);
        Assert.True(result.ServingComparisonReady); // expected group still fully Valid
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(ServingSummaryStatus.Valid, cross.Status);
        Assert.Equal(20, cross.Metrics!.MinSeconds); // 999 never influences the median
    }

    [Fact]
    public void UnexpectedOperationOrStratum_IntegrityInvalid()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            ValidRep("S1", "Hit", 1, 1, 10),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30),
            ValidRep("S6", "Bogus", 1, 1, 5));
        Assert.False(result.InputIntegrityValid);
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.UnexpectedRepetitionSummary);
        Assert.True(result.ServingComparisonReady);
    }

    [Fact]
    public void ShapeFailures_ExpectedCountMismatch_Detected()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            Raw("S1", "Hit", 1, ServingSummaryStatus.Valid, expected: 5, Array.Empty<ServingIncompleteReason>(), 1, 1, 0, 0, 0, Metrics(1, 10)),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.Equal(ServingCrossIntegrityCode.ExpectedCountMismatch, Assert.Single(result.IntegrityProblems).Code);
    }

    [Fact]
    public void ValidWithReasons_IntegrityProblem()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            Raw("S1", "Hit", 1, ServingSummaryStatus.Valid, 1, new[] { ServingIncompleteReason.EnvelopeNotValid }, 1, 1, 0, 0, 0, Metrics(1, 10)),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.ValidSummaryHasReasons);
        Assert.False(result.InputIntegrityValid);
    }

    [Fact]
    public void ValidWithNullMetrics_IntegrityProblem()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            Raw("S1", "Hit", 1, ServingSummaryStatus.Valid, 1, Array.Empty<ServingIncompleteReason>(), 1, 1, 0, 0, 0, null),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.ValidSummaryMissingMetrics);
        Assert.Equal(ServingSummaryStatus.Incomplete, Assert.Single(result.CrossSummaries).Status);
        Assert.Null(Assert.Single(result.CrossSummaries).Metrics);
    }

    [Fact]
    public void ValidInconsistentCounts_IntegrityProblem()
    {
        var workload = Workload(("S1", "Hit"));
        var result = Calc(workload,
            Raw("S1", "Hit", 1, ServingSummaryStatus.Valid, 1, Array.Empty<ServingIncompleteReason>(), 1, 0, 0, 0, 0, Metrics(1, 10)),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.ValidSummaryCountsMismatch);
    }

    [Fact]
    public void IncompleteWithMetricsOrEmptyReasons_IntegrityProblem()
    {
        var workload = Workload(("S1", "Hit"));
        var withMetrics = Calc(workload,
            Raw("S1", "Hit", 1, ServingSummaryStatus.Incomplete, 1, new[] { ServingIncompleteReason.ErrorSample }, 1, 0, 0, 0, 1, Metrics(1, 10)),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.Contains(withMetrics.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.IncompleteSummaryHasMetrics);

        var emptyReasons = Calc(workload,
            Raw("S1", "Hit", 1, ServingSummaryStatus.Incomplete, 1, Array.Empty<ServingIncompleteReason>(), 0, 0, 0, 0, 0, null),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.Contains(emptyReasons.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.IncompleteSummaryMissingReasons);
    }

    [Fact]
    public void TailProbe_CreatesNoCrossKey()
    {
        var workload = WorkloadWithTail();
        var result = Calc(workload,
            ValidRep("S1", "Hit", 1, 1, 10),
            ValidRep("S1", "Hit", 2, 1, 20),
            ValidRep("S1", "Hit", 3, 1, 30));
        Assert.True(result.InputIntegrityValid);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal("Hit", cross.Stratum);
        Assert.True(result.ServingComparisonReady);
    }

    [Fact]
    public void EmptyExpectedSet_FailsClosed()
    {
        var workload = Workload();
        var result = Calc(workload, ValidRep("S1", "Hit", 1, 1, 10));
        Assert.False(result.InputIntegrityValid);
        Assert.False(result.ServingComparisonReady);
        Assert.Contains(result.IntegrityProblems, p => p.Code == ServingCrossIntegrityCode.NoExpectedCrossSummaries);
        Assert.Empty(result.CrossSummaries);
    }
}

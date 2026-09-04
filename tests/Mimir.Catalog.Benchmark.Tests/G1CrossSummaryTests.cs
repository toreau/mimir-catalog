using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class G1CrossSummaryTests
{
    private static GraphWorkload Workload(params string[] strata)
    {
        var probes = new List<GraphProbe>();
        var expected = new Dictionary<(string, long), GraphExpected>();
        long seq = 0;
        foreach (var stratum in strata)
        {
            probes.Add(new GraphProbe("G1", seq, stratum, true, 1000 + seq));
            expected[("G1", seq)] = new GraphExpected("G1", seq, true, 0, 1, "d");
            seq++;
        }
        return new GraphWorkload { Probes = probes, Expected = expected };
    }

    private static G1SummaryMetrics Metrics(double v) => new(1, v, v, v, v, v, v, v, 1);

    private static G1SummaryMetrics Metrics8(double min, double p50, double p90, double p95, double p99, double max, double mean, double throughput)
        => new(1, min, p50, p90, p95, p99, max, mean, throughput);

    private static G1RepetitionSummary ValidRep(string stratum, int rep, double v)
        => new("G1", stratum, rep, G1SummaryStatus.Valid, Array.Empty<G1IncompleteReason>(), 1, 1, 1, 0, 0, 0, Metrics(v));

    private static G1RepetitionSummary IncompleteRep(string stratum, int rep, params G1IncompleteReason[] reasons)
        => new("G1", stratum, rep, G1SummaryStatus.Incomplete, reasons, 1, 0, 0, 0, 0, 0, null);

    private static G1RepetitionSummary Raw(string stratum, int rep, G1SummaryStatus status, long expected,
        IReadOnlyList<G1IncompleteReason> reasons, long observed, long valid, G1SummaryMetrics? metrics)
        => new("G1", stratum, rep, status, reasons, expected, observed, valid, 0, 0, 0, metrics);

    private static G1CrossCalculationResult Calc(GraphWorkload workload, params G1RepetitionSummary[] summaries)
        => G1CrossSummaryCalculator.Calculate(workload, summaries);

    [Fact]
    public void ThreeValid_ValidCross_ExactMedians()
    {
        var result = Calc(Workload("Degree1"),
            ValidRep("Degree1", 1, 10),
            ValidRep("Degree1", 2, 20),
            ValidRep("Degree1", 3, 30));
        Assert.True(result.InputIntegrityValid);
        Assert.True(result.G1ComparisonReady);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(G1CrossSummaryStatus.Valid, cross.Status);
        Assert.Equal(3, cross.ValidRepetitionCount);
        Assert.Empty(cross.IncompleteRepetitions);
        Assert.Equal(1, cross.ExpectedCount);
        Assert.Equal(20, cross.Metrics!.MinSeconds);
    }

    [Fact]
    public void TwoStrata_AllValid_ReadyTrue_Ordered()
    {
        var workload = Workload("Degree1", "Degree2Plus");
        var result = Calc(workload,
            ValidRep("Degree2Plus", 1, 1), ValidRep("Degree2Plus", 2, 2), ValidRep("Degree2Plus", 3, 3),
            ValidRep("Degree1", 1, 10), ValidRep("Degree1", 2, 20), ValidRep("Degree1", 3, 30));
        Assert.True(result.G1ComparisonReady);
        Assert.Equal(new[] { "Degree1", "Degree2Plus" }, result.CrossSummaries.Select(c => c.Stratum).ToArray());
    }

    [Fact]
    public void AllEightMetrics_MedianIndependent()
    {
        var m1 = Metrics8(1, 10, 20, 30, 40, 50, 60, 0.1);
        var m2 = Metrics8(2, 11, 21, 31, 41, 51, 61, 0.2);
        var m3 = Metrics8(3, 12, 22, 32, 42, 52, 62, 0.3);
        G1RepetitionSummary R(int rep, G1SummaryMetrics m) => new("G1", "Degree1", rep, G1SummaryStatus.Valid,
            Array.Empty<G1IncompleteReason>(), 1, 1, 1, 0, 0, 0, m);
        var result = Calc(Workload("Degree1"), R(1, m1), R(2, m2), R(3, m3));
        var metrics = Assert.Single(result.CrossSummaries).Metrics!;
        Assert.Equal(2, metrics.MinSeconds);
        Assert.Equal(11, metrics.P50Seconds);
        Assert.Equal(21, metrics.P90Seconds);
        Assert.Equal(31, metrics.P95Seconds);
        Assert.Equal(41, metrics.P99Seconds);
        Assert.Equal(51, metrics.MaxSeconds);
        Assert.Equal(61, metrics.MeanSeconds);
        Assert.Equal(0.2, metrics.ThroughputPerSecond);
        Assert.Equal(1, metrics.Count);
    }

    [Fact]
    public void LegitIncomplete_NoSurvivor()
    {
        var result = Calc(Workload("Degree1"),
            ValidRep("Degree1", 1, 10),
            IncompleteRep("Degree1", 2, G1IncompleteReason.TimeoutSample),
            ValidRep("Degree1", 3, 30));
        Assert.True(result.InputIntegrityValid);
        Assert.False(result.G1ComparisonReady);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(G1CrossSummaryStatus.Incomplete, cross.Status);
        Assert.Null(cross.Metrics);
        Assert.Equal(2, cross.ValidRepetitionCount);
        Assert.Equal(new[] { 2 }, cross.IncompleteRepetitions);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingRep_IntegrityInvalid(int missing)
    {
        var reps = new List<G1RepetitionSummary>();
        for (int rep = 1; rep <= 3; rep++)
            if (rep != missing) reps.Add(ValidRep("Degree1", rep, rep * 10));
        var result = Calc(Workload("Degree1"), reps.ToArray());
        Assert.False(result.InputIntegrityValid);
        Assert.False(result.G1ComparisonReady);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.MissingRepetitionSummary && p.Repetition == missing);
        Assert.Equal(G1CrossSummaryStatus.Incomplete, Assert.Single(result.CrossSummaries).Status);
        Assert.Equal(0, Assert.Single(result.CrossSummaries).ValidRepetitionCount);
        Assert.Empty(Assert.Single(result.CrossSummaries).IncompleteRepetitions);
    }

    [Fact]
    public void DuplicateExpectedRep_CorruptMatrix()
    {
        var result = Calc(Workload("Degree1"),
            ValidRep("Degree1", 1, 10),
            ValidRep("Degree1", 2, 20),
            ValidRep("Degree1", 2, 21),
            ValidRep("Degree1", 3, 30));
        Assert.False(result.InputIntegrityValid);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.DuplicateRepetitionSummary && p.Repetition == 2);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(G1CrossSummaryStatus.Incomplete, cross.Status);
        Assert.Equal(0, cross.ValidRepetitionCount);
        Assert.Empty(cross.IncompleteRepetitions);
        Assert.Null(cross.Metrics);
    }

    [Fact]
    public void ExtraRep0AndRep4_DoNotContaminateMatrix()
    {
        var result = Calc(Workload("Degree1"),
            ValidRep("Degree1", 1, 10),
            ValidRep("Degree1", 2, 20),
            ValidRep("Degree1", 3, 30),
            ValidRep("Degree1", 0, 5),
            ValidRep("Degree1", 4, 999));
        Assert.False(result.InputIntegrityValid);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.UnexpectedRepetitionNumber && p.Repetition == 0);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.UnexpectedRepetitionNumber && p.Repetition == 4);
        Assert.True(result.G1ComparisonReady); // expected matrix untouched
        Assert.Equal(20, Assert.Single(result.CrossSummaries).Metrics!.MinSeconds);
    }

    [Fact]
    public void UnexpectedStratumOrOperation_Deduplicated()
    {
        var result = Calc(Workload("Degree1"),
            ValidRep("Degree1", 1, 10),
            ValidRep("Degree1", 2, 20),
            ValidRep("Degree1", 3, 30),
            ValidRep("Bogus", 1, 1),
            ValidRep("Bogus", 1, 1));
        Assert.False(result.InputIntegrityValid);
        Assert.Single(result.IntegrityProblems.Where(p => p.Code == G1CrossIntegrityCode.UnexpectedRepetitionSummary));
        Assert.True(result.G1ComparisonReady);
    }

    [Fact]
    public void MalformedValid_DoesNotCountAnywhere()
    {
        var malformed = Raw("Degree1", 2, G1SummaryStatus.Valid, 1,
            Array.Empty<G1IncompleteReason>(), 1, 1, Metrics(20)); // Reasons empty but metric count ok -> shape-valid
        // Build a genuinely malformed variant: count mismatch and reasons.
        var bad = Raw("Degree1", 2, G1SummaryStatus.Valid, 1,
            new[] { G1IncompleteReason.EnvelopeNotValid }, 1, 1, Metrics(20));
        var result = Calc(Workload("Degree1"),
            ValidRep("Degree1", 1, 10),
            bad,
            ValidRep("Degree1", 3, 30));
        Assert.False(result.InputIntegrityValid);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.ValidSummaryHasReasons);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(G1CrossSummaryStatus.Incomplete, cross.Status); // malformed Valid record (in reason test) prevents metrics
        Assert.Null(cross.Metrics);
        Assert.Equal(2, cross.ValidRepetitionCount); // malformed rep2 does NOT inflate
        Assert.DoesNotContain(2, cross.IncompleteRepetitions); // nor enters incomplete list
        _ = malformed;
    }

    [Fact]
    public void MalformedIncomplete_NotInIncompleteRepetitions()
    {
        var bad = Raw("Degree1", 2, G1SummaryStatus.Incomplete, 1,
            Array.Empty<G1IncompleteReason>(), 0, 0, null);
        var result = Calc(Workload("Degree1"),
            ValidRep("Degree1", 1, 10),
            bad,
            ValidRep("Degree1", 3, 30));
        Assert.False(result.InputIntegrityValid);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.IncompleteSummaryMissingReasons);
        var cross = Assert.Single(result.CrossSummaries);
        Assert.Equal(G1CrossSummaryStatus.Incomplete, cross.Status);
        Assert.Equal(2, cross.ValidRepetitionCount);
        Assert.DoesNotContain(2, cross.IncompleteRepetitions);
    }

    [Fact]
    public void ExpectedCountMismatch_IntegrityProblem()
    {
        var bad = Raw("Degree1", 1, G1SummaryStatus.Valid, 99, Array.Empty<G1IncompleteReason>(), 1, 1, Metrics(10));
        var result = Calc(Workload("Degree1"), bad, ValidRep("Degree1", 2, 20), ValidRep("Degree1", 3, 30));
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.ExpectedCountMismatch);
        Assert.False(result.InputIntegrityValid);
        Assert.Equal(G1CrossSummaryStatus.Incomplete, Assert.Single(result.CrossSummaries).Status);
    }

    [Fact]
    public void ValidMissingMetrics_AndCountsMismatch_Problems()
    {
        var missing = Raw("Degree1", 1, G1SummaryStatus.Valid, 1, Array.Empty<G1IncompleteReason>(), 1, 1, null);
        var result = Calc(Workload("Degree1"), missing, ValidRep("Degree1", 2, 20), ValidRep("Degree1", 3, 30));
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.ValidSummaryMissingMetrics);

        var badCounts = Raw("Degree1", 1, G1SummaryStatus.Valid, 1, Array.Empty<G1IncompleteReason>(), 1, 0, Metrics(10));
        var result2 = Calc(Workload("Degree1"), badCounts, ValidRep("Degree1", 2, 20), ValidRep("Degree1", 3, 30));
        Assert.Contains(result2.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.ValidSummaryCountsMismatch);
    }

    [Fact]
    public void IncompleteWithMetrics_IntegrityProblem()
    {
        var bad = Raw("Degree1", 1, G1SummaryStatus.Incomplete, 1,
            new[] { G1IncompleteReason.ErrorSample }, 1, 0, Metrics(10));
        var result = Calc(Workload("Degree1"), bad, ValidRep("Degree1", 2, 20), ValidRep("Degree1", 3, 30));
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.IncompleteSummaryHasMetrics);
        Assert.False(result.InputIntegrityValid);
    }

    [Fact]
    public void UnknownOperations_SortOrdinally_RegardlessOfInputOrder()
    {
        var workload = Workload("Degree1");
        var valid = new[] { ValidRep("Degree1", 1, 10), ValidRep("Degree1", 2, 20), ValidRep("Degree1", 3, 30) };
        var zzz = Unknown("ZZZ", "Bogus", 1);
        var aaa = Unknown("AAA", "Bogus", 1);
        var result = G1CrossSummaryCalculator.Calculate(workload, valid.Concat(new[] { zzz, aaa }).ToArray());
        var ops = result.IntegrityProblems.Select(p => p.Operation).Where(o => o != "G1").ToList();
        Assert.Equal(new[] { "AAA", "ZZZ" }, ops);
        Assert.True(result.G1ComparisonReady); // expected matrix untouched
    }

    [Fact]
    public void InputPermutation_DoesNotChangeProblemOrdering()
    {
        var workload = Workload("Degree1");
        var valid = new[] { ValidRep("Degree1", 1, 10), ValidRep("Degree1", 2, 20), ValidRep("Degree1", 3, 30) };
        var aaa = Unknown("AAA", "Bogus", 1);
        var zzz = Unknown("ZZZ", "Bogus", 1);

        IEnumerable<(string, string, int, G1CrossIntegrityCode)> Keys(G1CrossCalculationResult r)
            => r.IntegrityProblems.Select(p => (p.Operation, p.Stratum, p.Repetition ?? -1, p.Code));

        var first = G1CrossSummaryCalculator.Calculate(workload, valid.Concat(new[] { aaa, zzz }).ToArray());
        var second = G1CrossSummaryCalculator.Calculate(workload, valid.Concat(new[] { zzz, aaa }).ToArray());
        Assert.Equal(Keys(first), Keys(second));
    }

    [Fact]
    public void G1Problems_SortBeforeUnknownOperations()
    {
        var workload = Workload("Degree1");
        var valid = new[] { ValidRep("Degree1", 1, 10), ValidRep("Degree1", 2, 20) }; // missing rep 3 -> G1 problem
        var result = G1CrossSummaryCalculator.Calculate(workload, valid.Concat(new[] { Unknown("ZZZ", "Bogus", 1), Unknown("AAA", "Bogus", 1) }).ToArray());
        var ops = result.IntegrityProblems.Select(p => p.Operation).ToList();
        Assert.True(ops.IndexOf("G1") < ops.IndexOf("AAA"));
        Assert.True(ops.IndexOf("AAA") < ops.IndexOf("ZZZ"));
    }

    [Fact]
    public void SubordinateOrdering_StrataOrdinal_Remains()
    {
        // Same G1 operation; expected groups Degree1/Degree2Plus both miss rep 2,
        // so stratum ordinal determines ordering for otherwise-equal problems.
        var probes = new List<GraphProbe>
        {
            new("G1", 0, "Degree2Plus", true, 1000),
            new("G1", 1, "Degree1", true, 1001),
        };
        var expected = new Dictionary<(string, long), GraphExpected>
        {
            [("G1", 0L)] = new("G1", 0, true, 0, 1, "d"),
            [("G1", 1L)] = new("G1", 1, true, 0, 1, "d"),
        };
        var workload = new GraphWorkload { Probes = probes, Expected = expected };
        var reps = new[]
        {
            ValidRep("Degree1", 1, 10), ValidRep("Degree1", 3, 30),
            ValidRep("Degree2Plus", 1, 10), ValidRep("Degree2Plus", 3, 30),
        };
        var result = G1CrossSummaryCalculator.Calculate(workload, reps);
        var strata = result.IntegrityProblems.Where(p => p.Code == G1CrossIntegrityCode.MissingRepetitionSummary)
            .Select(p => p.Stratum).ToList();
        Assert.Equal(new[] { "Degree1", "Degree2Plus" }, strata);
    }

    private static G1RepetitionSummary Unknown(string op, string stratum, int rep)
        => new(op, stratum, rep, G1SummaryStatus.Incomplete,
            new[] { G1IncompleteReason.NotAttemptedDueToHalt }, 1, 0, 0, 0, 0, 0, null);

    [Fact]
    public void ZeroExpectedGroups_FailsClosed()
    {
        var result = Calc(Workload(), ValidRep("Degree1", 1, 10));
        Assert.False(result.InputIntegrityValid);
        Assert.False(result.G1ComparisonReady);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G1CrossIntegrityCode.NoExpectedCrossSummaries);
        Assert.Empty(result.CrossSummaries);
    }
}

using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class G2CrossSummaryTests
{
    private static G2RepetitionSummary ValidRep(int rep, int expected = 200, double wall = 10.0, string? operation = "G2")
        => new(operation ?? "G2", rep, G2SummaryStatus.Valid, Array.Empty<G2IncompleteReason>(),
            expected, expected, ServingStatuses.Valid, TimedResultStatus.Valid, wall, wall);

    private static G2RepetitionSummary IncompleteRep(int rep, G2IncompleteReason reason, double? diagnosticWall = null,
        TimedResultStatus? timed = null, string? child = "VALID", int expected = 200)
        => new("G2", rep, G2SummaryStatus.Incomplete, new[] { reason }, expected, 0, child, timed, null, diagnosticWall);

    private static G2RepetitionSummary Raw(int rep, G2SummaryStatus status, IReadOnlyList<G2IncompleteReason> reasons,
        int expected, int observed, string? child, TimedResultStatus? timed, double? authoritative, double? diagnostic,
        string? operation = "G2")
        => new(operation ?? "G2", rep, status, reasons, expected, observed, child, timed, authoritative, diagnostic);

    private static G2CrossCalculationResult Calc(params G2RepetitionSummary[] summaries)
        => G2CrossSummaryCalculator.Calculate(summaries);

    [Fact]
    public void ThreeValid_PerfectMedian_ReadyTrue()
    {
        var result = Calc(ValidRep(1, wall: 10), ValidRep(2, wall: 20), ValidRep(3, wall: 30));
        Assert.True(result.InputIntegrityValid);
        Assert.True(result.G2ComparisonReady);
        Assert.Equal(G2CrossSummaryStatus.Valid, result.CrossSummary.Status);
        Assert.Equal(200, result.CrossSummary.ExpectedPerInputCount);
        Assert.Equal(3, result.CrossSummary.ValidRepetitionCount);
        Assert.Empty(result.CrossSummary.IncompleteRepetitions);
        Assert.Equal(20, result.CrossSummary.MedianBatchWallSeconds);
    }

    [Fact]
    public void UnorderedInput_SameMedian()
    {
        var result = Calc(ValidRep(3, wall: 30), ValidRep(1, wall: 10), ValidRep(2, wall: 20));
        Assert.Equal(20, result.CrossSummary.MedianBatchWallSeconds);
        Assert.True(result.G2ComparisonReady);
    }

    [Theory]
    [InlineData(G2IncompleteReason.TimeoutBatch, 130.0)]
    [InlineData(G2IncompleteReason.InvalidBatch, 5.0)]
    [InlineData(G2IncompleteReason.ErrorBatch, 7.0)]
    [InlineData(G2IncompleteReason.NotAttemptedDueToHalt, null)]
    public void LegitIncomplete_NoSurvivor(G2IncompleteReason reason, double? diagWall)
    {
        var result = Calc(ValidRep(1, wall: 10),
            IncompleteRep(2, reason, diagnosticWall: diagWall, timed: reason == G2IncompleteReason.NotAttemptedDueToHalt ? null : TimedResultStatus.Valid),
            ValidRep(3, wall: 30));
        Assert.True(result.InputIntegrityValid);
        Assert.False(result.G2ComparisonReady);
        Assert.Equal(G2CrossSummaryStatus.Incomplete, result.CrossSummary.Status);
        Assert.Null(result.CrossSummary.MedianBatchWallSeconds);
        Assert.Equal(200, result.CrossSummary.ExpectedPerInputCount); // counts still agree
        Assert.Equal(2, result.CrossSummary.ValidRepetitionCount);
        Assert.Equal(new[] { 2 }, result.CrossSummary.IncompleteRepetitions);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingRep_IntegrityInvalid(int missing)
    {
        var reps = new List<G2RepetitionSummary>();
        for (int rep = 1; rep <= 3; rep++)
            if (rep != missing) reps.Add(ValidRep(rep));
        var result = Calc(reps.ToArray());
        Assert.False(result.InputIntegrityValid);
        Assert.False(result.G2ComparisonReady);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G2CrossIntegrityCode.MissingRepetitionSummary && p.Repetition == missing);
        Assert.Equal(G2CrossSummaryStatus.Incomplete, result.CrossSummary.Status);
        Assert.Null(result.CrossSummary.ExpectedPerInputCount);
        Assert.Equal(0, result.CrossSummary.ValidRepetitionCount);
        Assert.Empty(result.CrossSummary.IncompleteRepetitions);
    }

    [Fact]
    public void AllExpectedAbsent_ThreeExplicitMissingProblems()
    {
        var result = Calc();
        Assert.Equal(3, result.IntegrityProblems.Count(p => p.Code == G2CrossIntegrityCode.MissingRepetitionSummary));
        Assert.True(result.IntegrityProblems.All(p => p.Repetition is not null));
        Assert.Equal(G2CrossSummaryStatus.Incomplete, result.CrossSummary.Status);
    }

    [Fact]
    public void DuplicateExpectedRep_CorruptedMatrix()
    {
        var result = Calc(ValidRep(1), ValidRep(2), ValidRep(2, wall: 21), ValidRep(3));
        Assert.Contains(result.IntegrityProblems, p => p.Code == G2CrossIntegrityCode.DuplicateRepetitionSummary && p.Repetition == 2);
        Assert.Equal(G2CrossSummaryStatus.Incomplete, result.CrossSummary.Status);
        Assert.Null(result.CrossSummary.ExpectedPerInputCount);
        Assert.Equal(0, result.CrossSummary.ValidRepetitionCount);
        Assert.Empty(result.CrossSummary.IncompleteRepetitions);
        Assert.Null(result.CrossSummary.MedianBatchWallSeconds);
    }

    [Fact]
    public void ExtraRep0And4_DoNotContaminatePerfectMatrix()
    {
        var result = Calc(ValidRep(1, wall: 10), ValidRep(2, wall: 20), ValidRep(3, wall: 30), ValidRep(0), ValidRep(4, wall: 999));
        Assert.False(result.InputIntegrityValid);
        Assert.True(result.G2ComparisonReady);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G2CrossIntegrityCode.UnexpectedRepetitionNumber && p.Repetition == 0);
        Assert.Contains(result.IntegrityProblems, p => p.Code == G2CrossIntegrityCode.UnexpectedRepetitionNumber && p.Repetition == 4);
        Assert.Equal(20, result.CrossSummary.MedianBatchWallSeconds);
    }

    [Fact]
    public void UnexpectedOperations_DedupAndOrdinal()
    {
        var result = Calc(ValidRep(1), ValidRep(2), ValidRep(3),
            Raw(1, G2SummaryStatus.Incomplete, new[] { G2IncompleteReason.NotAttemptedDueToHalt }, 200, 0, null, null, null, null, "ZZZ"),
            Raw(1, G2SummaryStatus.Incomplete, new[] { G2IncompleteReason.NotAttemptedDueToHalt }, 200, 0, null, null, null, null, "ZZZ"),
            Raw(1, G2SummaryStatus.Incomplete, new[] { G2IncompleteReason.NotAttemptedDueToHalt }, 200, 0, null, null, null, null, "AAA"));
        var unknown = result.IntegrityProblems.Where(p => p.Code == G2CrossIntegrityCode.UnexpectedRepetitionSummary)
            .Select(p => p.Operation).ToList();
        Assert.Equal(new[] { "AAA", "ZZZ" }, unknown);
        Assert.True(result.G2ComparisonReady);
    }

    [Fact]
    public void InputPermutation_IdenticalProblemOrdering()
    {
        var g2Valid = new[] { ValidRep(1), ValidRep(2), ValidRep(3) };
        var zzz = Raw(1, G2SummaryStatus.Incomplete, new[] { G2IncompleteReason.NotAttemptedDueToHalt }, 200, 0, null, null, null, null, "ZZZ");
        var aaa = Raw(1, G2SummaryStatus.Incomplete, new[] { G2IncompleteReason.NotAttemptedDueToHalt }, 200, 0, null, null, null, null, "AAA");
        IEnumerable<(string, int, G2CrossIntegrityCode)> Keys(G2CrossCalculationResult r)
            => r.IntegrityProblems.Select(p => (p.Operation, p.Repetition ?? -1, p.Code));
        var first = Calc(g2Valid.Concat(new[] { zzz, aaa }).ToArray());
        var second = Calc(g2Valid.Concat(new[] { aaa, zzz }).ToArray());
        Assert.Equal(Keys(first), Keys(second));
    }

    [Fact]
    public void G2Problems_SortBeforeUnknownOperations()
    {
        var result = Calc(ValidRep(1), ValidRep(3), // missing rep 2 -> G2 problem
            Raw(1, G2SummaryStatus.Incomplete, new[] { G2IncompleteReason.NotAttemptedDueToHalt }, 200, 0, null, null, null, null, "ZZZ"));
        var ops = result.IntegrityProblems.Select(p => p.Operation).ToList();
        Assert.True(ops.IndexOf("G2") < ops.IndexOf("ZZZ"));
    }

    [Fact]
    public void ExpectedCountDisagreement_NoAuthority_NoMedian()
    {
        var result = Calc(ValidRep(1, expected: 200), ValidRep(2, expected: 201), ValidRep(3, expected: 200));
        Assert.Single(result.IntegrityProblems);
        var problem = result.IntegrityProblems[0];
        Assert.Equal(G2CrossIntegrityCode.ExpectedPerInputCountMismatch, problem.Code);
        Assert.Null(problem.Repetition);
        Assert.Equal(G2CrossSummaryStatus.Incomplete, result.CrossSummary.Status);
        Assert.Null(result.CrossSummary.ExpectedPerInputCount);
        Assert.Null(result.CrossSummary.MedianBatchWallSeconds);
        Assert.Equal(3, result.CrossSummary.ValidRepetitionCount); // individually shape-valid
    }

    [Fact]
    public void ValidShapeViolations_Detected()
    {
        var withReasons = Raw(1, G2SummaryStatus.Valid, new[] { G2IncompleteReason.EnvelopeNotValid }, 200, 200, "VALID", TimedResultStatus.Valid, 10, 10);
        var observedMismatch = Raw(1, G2SummaryStatus.Valid, Array.Empty<G2IncompleteReason>(), 200, 199, "VALID", TimedResultStatus.Valid, 10, 10);
        var missingAuth = Raw(1, G2SummaryStatus.Valid, Array.Empty<G2IncompleteReason>(), 200, 200, "VALID", TimedResultStatus.Valid, null, 10);
        var missingDiag = Raw(1, G2SummaryStatus.Valid, Array.Empty<G2IncompleteReason>(), 200, 200, "VALID", TimedResultStatus.Valid, 10, null);
        var wallMismatch = Raw(1, G2SummaryStatus.Valid, Array.Empty<G2IncompleteReason>(), 200, 200, "VALID", TimedResultStatus.Valid, 10, 11);
        var badTimed = Raw(1, G2SummaryStatus.Valid, Array.Empty<G2IncompleteReason>(), 200, 200, "VALID", TimedResultStatus.Timeout, 10, 10);
        var badChild = Raw(1, G2SummaryStatus.Valid, Array.Empty<G2IncompleteReason>(), 200, 200, "INVALID", TimedResultStatus.Valid, 10, 10);

        Assert.Contains(Calc(withReasons, ValidRep(2), ValidRep(3)).IntegrityProblems, p => p.Code == G2CrossIntegrityCode.ValidSummaryHasReasons);
        Assert.Contains(Calc(observedMismatch, ValidRep(2), ValidRep(3)).IntegrityProblems, p => p.Code == G2CrossIntegrityCode.ValidSummaryObservedCountMismatch);
        Assert.Contains(Calc(missingAuth, ValidRep(2), ValidRep(3)).IntegrityProblems, p => p.Code == G2CrossIntegrityCode.ValidSummaryMissingAuthoritativeWall);
        Assert.Contains(Calc(missingDiag, ValidRep(2), ValidRep(3)).IntegrityProblems, p => p.Code == G2CrossIntegrityCode.ValidSummaryMissingDiagnosticWall);
        Assert.Contains(Calc(wallMismatch, ValidRep(2), ValidRep(3)).IntegrityProblems, p => p.Code == G2CrossIntegrityCode.ValidSummaryWallMismatch);
        Assert.Contains(Calc(badTimed, ValidRep(2), ValidRep(3)).IntegrityProblems, p => p.Code == G2CrossIntegrityCode.ValidSummaryTimedStatusNotValid);
        Assert.Contains(Calc(badChild, ValidRep(2), ValidRep(3)).IntegrityProblems, p => p.Code == G2CrossIntegrityCode.ValidSummaryChildCorrectnessNotValid);
    }

    [Fact]
    public void IncompleteShapeViolations_Detected()
    {
        var noReasons = Raw(1, G2SummaryStatus.Incomplete, Array.Empty<G2IncompleteReason>(), 200, 0, null, null, null, null);
        var withAuthWall = Raw(1, G2SummaryStatus.Incomplete, new[] { G2IncompleteReason.ErrorBatch }, 200, 0, "ERROR", TimedResultStatus.Error, 10, 10);
        Assert.Contains(Calc(noReasons, ValidRep(2), ValidRep(3)).IntegrityProblems, p => p.Code == G2CrossIntegrityCode.IncompleteSummaryMissingReasons);
        Assert.Contains(Calc(withAuthWall, ValidRep(2), ValidRep(3)).IntegrityProblems, p => p.Code == G2CrossIntegrityCode.IncompleteSummaryHasAuthoritativeWall);
    }

    [Fact]
    public void MalformedRecords_CountNowhere()
    {
        // malformed Valid (reasons present) and malformed Incomplete (no reasons)
        var badValid = Raw(2, G2SummaryStatus.Valid, new[] { G2IncompleteReason.EnvelopeNotValid }, 200, 200, "VALID", TimedResultStatus.Valid, 10, 10);
        var badIncomplete = Raw(2, G2SummaryStatus.Incomplete, Array.Empty<G2IncompleteReason>(), 200, 0, null, null, null, null);
        var result = Calc(ValidRep(1), badValid, ValidRep(3));
        Assert.Equal(G2CrossSummaryStatus.Incomplete, result.CrossSummary.Status);
        Assert.Null(result.CrossSummary.MedianBatchWallSeconds);
        Assert.Equal(2, result.CrossSummary.ValidRepetitionCount); // malformed rep2 does not increment
        Assert.DoesNotContain(2, result.CrossSummary.IncompleteRepetitions);

        var result2 = Calc(ValidRep(1), badIncomplete, ValidRep(3));
        Assert.Equal(G2CrossSummaryStatus.Incomplete, result2.CrossSummary.Status);
        Assert.Equal(2, result2.CrossSummary.ValidRepetitionCount);
        Assert.DoesNotContain(2, result2.CrossSummary.IncompleteRepetitions);
    }
}

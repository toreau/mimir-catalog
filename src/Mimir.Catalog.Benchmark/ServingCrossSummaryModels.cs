namespace Mimir.Catalog.Benchmark;

public enum ServingCrossIntegrityCode
{
    NoExpectedCrossSummaries,
    MissingRepetitionSummary,
    DuplicateRepetitionSummary,
    UnexpectedRepetitionSummary,
    UnexpectedRepetitionNumber,
    ExpectedCountMismatch,
    ValidSummaryHasReasons,
    ValidSummaryMissingMetrics,
    ValidSummaryCountsMismatch,
    ValidMetricCountMismatch,
    IncompleteSummaryMissingReasons,
    IncompleteSummaryHasMetrics,
}

public sealed record ServingIntegrityProblem(
    string Operation,
    string Stratum,
    int? Repetition,
    ServingCrossIntegrityCode Code,
    string? Detail = null);

public sealed record ServingCrossSummary(
    string Operation,
    string Stratum,
    ServingSummaryStatus Status,
    long ExpectedCount,
    int ValidRepetitionCount,
    IReadOnlyList<int> IncompleteRepetitions,
    ServingSummaryMetrics? Metrics);

public sealed record ServingCrossCalculationResult(
    bool InputIntegrityValid,
    IReadOnlyList<ServingIntegrityProblem> IntegrityProblems,
    IReadOnlyList<ServingCrossSummary> CrossSummaries,
    bool ServingComparisonReady);

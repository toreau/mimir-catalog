namespace Mimir.Catalog.Benchmark;

public enum G1CrossSummaryStatus
{
    Valid,
    Incomplete,
}

public enum G1CrossIntegrityCode
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

public sealed record G1CrossIntegrityProblem(
    string Operation,
    string Stratum,
    int? Repetition,
    G1CrossIntegrityCode Code);

public sealed record G1CrossSummary(
    string Operation,
    string Stratum,
    G1CrossSummaryStatus Status,
    long ExpectedCount,
    int ValidRepetitionCount,
    IReadOnlyList<int> IncompleteRepetitions,
    G1SummaryMetrics? Metrics);

public sealed record G1CrossCalculationResult(
    bool InputIntegrityValid,
    IReadOnlyList<G1CrossIntegrityProblem> IntegrityProblems,
    IReadOnlyList<G1CrossSummary> CrossSummaries,
    bool G1ComparisonReady);

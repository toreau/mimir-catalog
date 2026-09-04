namespace Mimir.Catalog.Benchmark;

public enum G2CrossSummaryStatus
{
    Valid,
    Incomplete,
}

public enum G2CrossIntegrityCode
{
    MissingRepetitionSummary,
    DuplicateRepetitionSummary,
    UnexpectedRepetitionSummary,
    UnexpectedRepetitionNumber,
    ExpectedPerInputCountMismatch,
    ValidSummaryHasReasons,
    ValidSummaryObservedCountMismatch,
    ValidSummaryMissingAuthoritativeWall,
    ValidSummaryMissingDiagnosticWall,
    ValidSummaryWallMismatch,
    ValidSummaryTimedStatusNotValid,
    ValidSummaryChildCorrectnessNotValid,
    IncompleteSummaryMissingReasons,
    IncompleteSummaryHasAuthoritativeWall,
}

public sealed record G2CrossIntegrityProblem(
    string Operation,
    int? Repetition,
    G2CrossIntegrityCode Code);

public sealed record G2CrossSummary(
    string Operation,
    G2CrossSummaryStatus Status,
    int? ExpectedPerInputCount,
    int ValidRepetitionCount,
    IReadOnlyList<int> IncompleteRepetitions,
    double? MedianBatchWallSeconds);

public sealed record G2CrossCalculationResult(
    bool InputIntegrityValid,
    IReadOnlyList<G2CrossIntegrityProblem> IntegrityProblems,
    G2CrossSummary CrossSummary,
    bool G2ComparisonReady);

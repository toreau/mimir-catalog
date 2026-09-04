namespace Mimir.Catalog.Benchmark;

public enum G2SummaryStatus
{
    Valid,
    Incomplete,
}

public enum G2IncompleteReason
{
    ExecutionEvidenceInvalid,
    StableArtifactCaptureFailed,
    ProcessIncomplete,
    EnvelopeNotValid,
    TimedBatchIncomplete,
    MissingBatch,
    InvalidBatch,
    TimeoutBatch,
    ErrorBatch,
    NotAttemptedDueToHalt,
}

/// <summary>
/// Deterministic one-child facts consumed by the G2 repetition-summary
/// calculator. Mirrors the closed one-child result axes without CLI types.
/// PerInput collection is not carried; only its observed count.
/// </summary>
public sealed record G2ChildSnapshot(
    bool EvidenceValid,
    bool RegisteredStableArtifacts,
    bool ProcessCompleted,
    string EnvelopeStatus,
    bool TimedBatchComplete,
    G2ParentBatch? Batch,
    int ObservedPerInputCount);

/// <summary>
/// One G2 repetition. BatchWallSeconds is the sole comparison-authoritative wall
/// and is non-null only when Status=Valid; ObservedDiagnosticWallSeconds may
/// retain a trustworthy observed wall for Invalid/Timeout/Error timed batches.
/// </summary>
public sealed record G2RepetitionSummary(
    string Operation,
    int Repetition,
    G2SummaryStatus Status,
    IReadOnlyList<G2IncompleteReason> Reasons,
    int ExpectedPerInputCount,
    int ObservedPerInputCount,
    string? ChildCorrectness,
    TimedResultStatus? TimedStatus,
    double? BatchWallSeconds,
    double? ObservedDiagnosticWallSeconds);

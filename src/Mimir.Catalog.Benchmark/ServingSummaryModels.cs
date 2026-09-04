namespace Mimir.Catalog.Benchmark;

public enum ServingSummaryStatus
{
    Valid,
    Incomplete,
}

public enum ServingIncompleteReason
{
    ExecutionEvidenceInvalid,
    StableArtifactCaptureFailed,
    ProcessIncomplete,
    EnvelopeNotValid,
    MeasuredSequenceIncomplete,
    MissingSamples,
    InvalidSample,
    TimeoutSample,
    ErrorSample,
    NotAttemptedDueToHalt,
}

public sealed record ServingSummaryMetrics(
    long Count,
    double MinSeconds,
    double P50Seconds,
    double P90Seconds,
    double P95Seconds,
    double P99Seconds,
    double MaxSeconds,
    double MeanSeconds,
    double ThroughputPerSecond);

/// <summary>
/// Deterministic child facts consumed by the repetition-summary calculator.
/// Mirrors the closed one-child result axes without referencing CLI types.
/// </summary>
public sealed record ServingChildSnapshot(
    bool EvidenceValid,
    bool RegisteredStableArtifacts,
    bool ProcessCompleted,
    string EnvelopeStatus,
    bool MeasuredSequenceComplete,
    IReadOnlyList<ServingParentSample> Samples);

public sealed record ServingRepetitionSummary(
    string Operation,
    string Stratum,
    int Repetition,
    ServingSummaryStatus Status,
    IReadOnlyList<ServingIncompleteReason> Reasons,
    long ExpectedCount,
    long ObservedCount,
    long ValidCount,
    long InvalidCount,
    long TimeoutCount,
    long ErrorCount,
    ServingSummaryMetrics? Metrics);

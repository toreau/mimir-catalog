namespace Mimir.Catalog.Benchmark;

/// <summary>Raw parsed per-input line from the deterministic G2 child artifact.</summary>
public sealed record G2RawPerInput(
    int Item,
    long Qid,
    string SourceStratum,
    string CorrectnessStatus,
    long? ActualCardinality = null,
    string? ActualDigest = null,
    string? Error = null);

/// <summary>Raw parsed Batch line from the deterministic G2 child artifact.</summary>
public sealed record G2RawBatch(
    double WallSeconds,
    string CorrectnessStatus,
    long? ActualCardinality = null,
    string? ActualDigest = null,
    string? Error = null);

/// <summary>Parsed G2 artifact; per-input then (optionally) one Batch, EOF.</summary>
public sealed record G2RawDocument(
    IReadOnlyList<G2RawPerInput> PerInput,
    G2RawBatch? Batch);

public enum G2CorrectnessVerdict
{
    ConfirmedValid,
    ConfirmedInvalid,
    ConfirmedError,
    IntegrityFailure,
}

/// <summary>Parent-classified G2 per-input fact. Correctness evidence only; no latency.</summary>
public sealed record G2ParentPerInput(
    int Item,
    long Qid,
    string SourceStratum,
    string ChildCorrectness,
    long? ActualCardinality = null,
    string? ActualDigest = null,
    string? Error = null);

/// <summary>Parent-classified G2 Batch fact with the only derived timed status.</summary>
public sealed record G2ParentBatch(
    double WallSeconds,
    TimedResultStatus Status,
    string ChildCorrectness,
    long? ActualCardinality = null,
    string? ActualDigest = null,
    string? Error = null);

/// <summary>Graph/G2 parent claim verification and Batch timeout classification.</summary>
public static class G2ParentClassifier
{
    /// <summary>
    /// Batch point-timeout precedence: ERROR &gt; INVALID &gt; (VALID &amp;&amp;
    /// wall &gt;= 120.0) &gt; Valid. Exactly 120.0 s is Timeout.
    /// </summary>
    public static TimedResultStatus PointStatus(string childCorrectness, double wallSeconds)
    {
        if (childCorrectness == ServingStatuses.Error) return TimedResultStatus.Error;
        if (childCorrectness == ServingStatuses.Invalid) return TimedResultStatus.Invalid;
        return wallSeconds >= 120.0 ? TimedResultStatus.Timeout : TimedResultStatus.Valid;
    }

    public static IReadOnlyList<string> VerifyPerInputClaim(G2RawPerInput raw, G2PerInputExpected expected)
    {
        var problems = new List<string>();
        switch (raw.CorrectnessStatus)
        {
            case ServingStatuses.Valid:
                if (raw.ActualCardinality is not { } card || raw.ActualDigest is null)
                {
                    problems.Add($"per-input item {raw.Item} claims VALID without actual cardinality/digest");
                    break;
                }
                if (raw.Error is not null) problems.Add($"per-input item {raw.Item} claims VALID but carries an error");
                if (card != expected.Cardinality) problems.Add($"per-input item {raw.Item} cardinality mismatch");
                if (raw.ActualDigest != expected.Digest) problems.Add($"per-input item {raw.Item} digest mismatch");
                break;
            case ServingStatuses.Invalid:
                if (raw.ActualCardinality is not { } icard || raw.ActualDigest is null)
                {
                    problems.Add($"per-input item {raw.Item} claims INVALID without actual cardinality/digest");
                    break;
                }
                if (raw.Error is not null) problems.Add($"per-input item {raw.Item} claims INVALID but carries an error");
                if (icard == expected.Cardinality && raw.ActualDigest == expected.Digest)
                    problems.Add($"per-input item {raw.Item} claims INVALID but actual equals expected");
                break;
            case ServingStatuses.Error:
                if (string.IsNullOrEmpty(raw.Error))
                    problems.Add($"per-input item {raw.Item} claims ERROR without an error message");
                if (raw.ActualCardinality is not null || raw.ActualDigest is not null)
                    problems.Add($"per-input item {raw.Item} claims ERROR but carries actual facts");
                break;
        }
        return problems;
    }

    public static G2CorrectnessVerdict PerInputVerdictOf(G2RawPerInput raw, G2PerInputExpected expected)
    {
        var problems = VerifyPerInputClaim(raw, expected);
        if (problems.Count > 0) return G2CorrectnessVerdict.IntegrityFailure;
        return raw.CorrectnessStatus switch
        {
            ServingStatuses.Valid => G2CorrectnessVerdict.ConfirmedValid,
            ServingStatuses.Invalid => G2CorrectnessVerdict.ConfirmedInvalid,
            _ => G2CorrectnessVerdict.ConfirmedError,
        };
    }

    /// <summary>
    /// Independent Batch claim verification plus cross-consistency against the
    /// independently confirmed per-input child statuses (positional, in order).
    /// </summary>
    public static IReadOnlyList<string> VerifyBatchClaim(
        G2RawBatch raw,
        G2BatchExpected expected,
        IReadOnlyList<string> perInputChildCorrectness)
    {
        var problems = new List<string>();
        bool anyPerInputError = perInputChildCorrectness.Contains(ServingStatuses.Error);
        bool anyPerInputInvalid = perInputChildCorrectness.Contains(ServingStatuses.Invalid);
        bool allPerInputValid = perInputChildCorrectness.All(c => c == ServingStatuses.Valid);

        switch (raw.CorrectnessStatus)
        {
            case ServingStatuses.Valid:
                if (raw.ActualCardinality is not { } card || raw.ActualDigest is null)
                {
                    problems.Add("batch claims VALID without actual cardinality/digest");
                    break;
                }
                if (raw.Error is not null) problems.Add("batch claims VALID but carries an error");
                if (card != expected.Cardinality) problems.Add("batch cardinality mismatch");
                if (raw.ActualDigest != expected.Digest) problems.Add("batch digest mismatch");
                if (!allPerInputValid) problems.Add("batch claims VALID but a per-input is not independently VALID");
                break;
            case ServingStatuses.Invalid:
                if (raw.ActualCardinality is not { } icard || raw.ActualDigest is null)
                {
                    problems.Add("batch claims INVALID without actual cardinality/digest");
                    break;
                }
                if (raw.Error is not null) problems.Add("batch claims INVALID but carries an error");
                if (anyPerInputError) problems.Add("batch claims INVALID while a per-input is ERROR");
                bool demonstrated = anyPerInputInvalid
                    || icard != expected.Cardinality
                    || raw.ActualDigest != expected.Digest;
                if (!demonstrated) problems.Add("batch claims INVALID without a demonstrated mismatch");
                break;
            case ServingStatuses.Error:
                if (string.IsNullOrEmpty(raw.Error))
                    problems.Add("batch claims ERROR without an error message");
                if (raw.ActualCardinality is not null || raw.ActualDigest is not null)
                    problems.Add("batch claims ERROR but carries actual facts");
                if (!anyPerInputError) problems.Add("batch claims ERROR without an independently ERROR per-input");
                break;
        }
        return problems;
    }
}

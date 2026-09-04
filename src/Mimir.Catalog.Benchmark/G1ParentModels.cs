namespace Mimir.Catalog.Benchmark;

public enum G1CorrectnessVerdict
{
    ConfirmedValid,
    ConfirmedInvalid,
    ConfirmedError,
    IntegrityFailure,
}

/// <summary>Parent-classified G1 sample: child correctness plus derived timed status.</summary>
public sealed record G1ParentSample(
    string Operation,
    long Sequence,
    string Stratum,
    double WallSeconds,
    TimedResultStatus Status,
    string ChildCorrectness,
    long? ActualCardinality = null,
    long? ActualVisited = null,
    string? ActualDigest = null,
    string? Error = null);

/// <summary>Graph/G1 parent claim verification and point-timeout classification.</summary>
public static class G1ParentClassifier
{
    /// <summary>
    /// Parent point-timeout precedence. Correctness outranks latency:
    /// ERROR &gt; INVALID &gt; (VALID &amp;&amp; wall &gt;= 30.0) &gt; Valid.
    /// Exactly 30.0 s is Timeout.
    /// </summary>
    public static TimedResultStatus PointStatus(string childCorrectness, double wallSeconds)
    {
        if (childCorrectness == ServingStatuses.Error) return TimedResultStatus.Error;
        if (childCorrectness == ServingStatuses.Invalid) return TimedResultStatus.Invalid;
        return wallSeconds >= 30.0 ? TimedResultStatus.Timeout : TimedResultStatus.Valid;
    }

    /// <summary>Independent child-claim verification against the frozen expected row.</summary>
    public static IReadOnlyList<string> VerifyClaim(G1TimedSample sample, GraphExpected expected)
    {
        var problems = new List<string>();
        switch (sample.CorrectnessStatus)
        {
            case ServingStatuses.Valid:
                if (sample.ActualCardinality is not { } card || sample.ActualVisited is not { } visited || sample.ActualDigest is null)
                {
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims VALID without actual cardinality/visited/digest");
                    break;
                }
                if (sample.Error is not null)
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims VALID but carries an error");
                if (card != expected.Cardinality) problems.Add($"sample ({sample.Operation},{sample.Sequence}) cardinality mismatch");
                if (visited != expected.Visited) problems.Add($"sample ({sample.Operation},{sample.Sequence}) visited mismatch");
                if (sample.ActualDigest != expected.Digest) problems.Add($"sample ({sample.Operation},{sample.Sequence}) digest mismatch");
                break;
            case ServingStatuses.Invalid:
                if (sample.ActualCardinality is not { } icard || sample.ActualVisited is not { } ivisited || sample.ActualDigest is null)
                {
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims INVALID without actual cardinality/visited/digest");
                    break;
                }
                if (sample.Error is not null)
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims INVALID but carries an error");
                if (icard == expected.Cardinality && ivisited == expected.Visited && sample.ActualDigest == expected.Digest)
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims INVALID but actual equals expected");
                break;
            case ServingStatuses.Error:
                if (string.IsNullOrEmpty(sample.Error))
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims ERROR without an error message");
                if (sample.ActualCardinality is not null || sample.ActualVisited is not null || sample.ActualDigest is not null)
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims ERROR but carries actual facts");
                break;
        }
        return problems;
    }

    public static G1CorrectnessVerdict VerdictOf(G1TimedSample sample, GraphExpected expected)
    {
        var problems = VerifyClaim(sample, expected);
        if (problems.Count > 0) return G1CorrectnessVerdict.IntegrityFailure;
        return sample.CorrectnessStatus switch
        {
            ServingStatuses.Valid => G1CorrectnessVerdict.ConfirmedValid,
            ServingStatuses.Invalid => G1CorrectnessVerdict.ConfirmedInvalid,
            _ => G1CorrectnessVerdict.ConfirmedError,
        };
    }
}

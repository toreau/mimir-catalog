using System.Globalization;

namespace Mimir.Catalog.Benchmark;

/// <summary>Parent-classified sample: child correctness plus derived timed status.</summary>
public sealed record ServingParentSample(
    string Operation,
    long Sequence,
    string Stratum,
    double WallSeconds,
    TimedResultStatus Status,
    string ChildCorrectness,
    long? ActualCardinality = null,
    string? ActualDigest = null,
    string? Error = null);

/// <summary>Outcome of one child sample's independent parent correctness verification.</summary>
public enum ServingCorrectnessVerdict
{
    ConfirmedValid,
    ConfirmedInvalid,
    ConfirmedError,
    IntegrityFailure,
}

public static class ServingParentClassifier
{
    /// <summary>
    /// Parent point-timeout precedence. Correctness failures outrank latency:
    /// ERROR &gt; INVALID &gt; (VALID &amp;&amp; wall &gt;= 5.0) &gt; Valid.
    /// Exactly 5.0 s is Timeout. Only Valid enters comparison statistics.
    /// </summary>
    public static TimedResultStatus PointStatus(string childCorrectness, double wallSeconds)
    {
        if (childCorrectness == ServingStatuses.Error) return TimedResultStatus.Error;
        if (childCorrectness == ServingStatuses.Invalid) return TimedResultStatus.Invalid;
        return wallSeconds >= 5.0 ? TimedResultStatus.Timeout : TimedResultStatus.Valid;
    }

    /// <summary>Independent child-claim verification against the frozen expected values.</summary>
    public static IReadOnlyList<string> VerifyClaim(ServingTimedSample sample, ServingExpected expected)
    {
        var problems = new List<string>();
        switch (sample.CorrectnessStatus)
        {
            case ServingStatuses.Valid:
                if (sample.ActualCardinality is not { } card || sample.ActualDigest is null)
                {
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims VALID without actual cardinality/digest");
                    break;
                }
                if (card != expected.Cardinality) problems.Add($"sample ({sample.Operation},{sample.Sequence}) cardinality mismatch");
                if (sample.ActualDigest != expected.Digest) problems.Add($"sample ({sample.Operation},{sample.Sequence}) digest mismatch");
                break;
            case ServingStatuses.Invalid:
                if (sample.ActualCardinality is not { } icard || sample.ActualDigest is null)
                {
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims INVALID without actual cardinality/digest");
                    break;
                }
                if (icard == expected.Cardinality && sample.ActualDigest == expected.Digest)
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims INVALID but actual equals expected");
                break;
            case ServingStatuses.Error:
                if (string.IsNullOrEmpty(sample.Error))
                    problems.Add($"sample ({sample.Operation},{sample.Sequence}) claims ERROR without an error message");
                break;
        }
        return problems;
    }

    public static ServingCorrectnessVerdict VerdictOf(ServingTimedSample sample, ServingExpected expected)
    {
        var problems = VerifyClaim(sample, expected);
        if (problems.Count > 0) return ServingCorrectnessVerdict.IntegrityFailure;
        return sample.CorrectnessStatus switch
        {
            ServingStatuses.Valid => ServingCorrectnessVerdict.ConfirmedValid,
            ServingStatuses.Invalid => ServingCorrectnessVerdict.ConfirmedInvalid,
            _ => ServingCorrectnessVerdict.ConfirmedError,
        };
    }
}

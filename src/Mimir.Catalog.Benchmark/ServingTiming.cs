using System.Diagnostics;

namespace Mimir.Catalog.Benchmark;

/// <summary>One raw timed serving probe sample. Never carries TIMEOUT.</summary>
public sealed record ServingTimedSample(
    string Operation,
    long Sequence,
    string Stratum,
    double WallSeconds,
    string CorrectnessStatus,
    long? ActualCardinality = null,
    string? ActualDigest = null,
    string? Error = null);

/// <summary>Result of one serving operation child execution.</summary>
public sealed class ServingTimingExecution
{
    public required string Operation { get; init; }
    public required int Repetition { get; init; }
    /// <summary>VALID / INVALID / ERROR for the whole warmup+timed+Tail correctness.</summary>
    public required string Correctness { get; init; }
    public required IReadOnlyList<ServingTimedSample> Samples { get; init; }
    /// <summary>Diagnostic wall around the complete timed-pass loop; not the per-stratum authority.</summary>
    public double? TimedPassWallSeconds { get; init; }
    /// <summary>Execution-level diagnostic category: warmup | timed-probe | tail.</summary>
    public string? ErrorCategory { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Candidate-neutral S1-S5 serving timing runner.
///
/// Per child: measured warmup pass (untimed but correctness-validated) then a
/// timed pass over the exact same measured sequence; S1 correctness-only Tail
/// probes run after the timed pass and never produce samples. The timer wraps
/// only candidate retrieval/materialization — candidate serving methods already
/// return fully materialized logical results, so no timed copy/enumeration is
/// introduced; canonicalization/digest and expected comparison happen after
/// StopSeconds. Samples keep every actual probe wall (including >= 5.0 s); the
/// child never emits TIMEOUT.
/// </summary>
public sealed class ServingTimingRunner
{
    private readonly IStorageCandidate _candidate;
    private readonly ServingWorkload _workload;
    private readonly string _operation;
    private readonly int _repetition;
    private readonly Func<ITimer> _timerFactory;

    public ServingTimingRunner(
        IStorageCandidate candidate,
        ServingWorkload workload,
        string operation,
        int repetition,
        Func<ITimer>? timerFactory = null)
    {
        _candidate = candidate;
        _workload = workload;
        _operation = operation;
        _repetition = repetition;
        _timerFactory = timerFactory ?? (() => new StopwatchTimer());
    }

    /// <summary>Exact measured (or correctness-only) probes for one operation, preserving published order.</summary>
    public static IReadOnlyList<ServingProbe> Select(IEnumerable<ServingProbe> probes, string operation, bool measuredOnly)
        => probes.Where(p => p.Op == operation && p.Measured == measuredOnly).ToList();

    public ServingTimingExecution Execute()
    {
        var measured = Select(_workload.Probes, _operation, measuredOnly: true);
        var expected = _workload.Expected;

        // Warmup: full correctness over every measured probe (untimed).
        (string warmupStatus, string? warmupError) = RunWarmup(measured, expected);
        if (warmupStatus != ServingStatuses.Valid)
        {
            return new ServingTimingExecution
            {
                Operation = _operation,
                Repetition = _repetition,
                Correctness = warmupStatus,
                Samples = Array.Empty<ServingTimedSample>(),
                TimedPassWallSeconds = null,
                ErrorCategory = warmupError is not null ? "warmup" : null,
                ErrorMessage = warmupError,
            };
        }

        // Timed pass over the exact same measured sequence.
        var samples = new List<ServingTimedSample>();
        bool sawInvalid = false;
        bool stoppedByError = false;
        string? timedError = null;
        var passClock = Stopwatch.StartNew();
        foreach (var probe in measured)
        {
            var expectedValue = expected[(probe.Op, probe.Seq)];

            var timer = _timerFactory();
            timer.Start();
            object? materialized = null;
            string? error = null;
            try
            {
                materialized = Materialize(probe); // candidate result is already fully materialized
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            double wall = timer.StopSeconds();

            if (error is not null)
            {
                samples.Add(new ServingTimedSample(_operation, probe.Seq, probe.Stratum, wall,
                    ServingStatuses.Error, Error: error));
                stoppedByError = true;
                timedError = error;
                break;
            }

            long cardinality;
            string digest;
            try
            {
                (cardinality, digest) = Canonicalize(probe, materialized!);
            }
            catch (Exception ex)
            {
                samples.Add(new ServingTimedSample(_operation, probe.Seq, probe.Stratum, wall,
                    ServingStatuses.Error, Error: ex.Message));
                stoppedByError = true;
                timedError = ex.Message;
                break;
            }

            bool valid = cardinality == expectedValue.Cardinality && digest == expectedValue.Digest;
            samples.Add(new ServingTimedSample(
                _operation, probe.Seq, probe.Stratum, wall,
                valid ? ServingStatuses.Valid : ServingStatuses.Invalid,
                cardinality, digest));
            if (!valid) sawInvalid = true;
        }
        passClock.Stop();
        double? passWall = measured.Count > 0 ? passClock.Elapsed.TotalSeconds : null;

        bool tailError = false;
        string? tailErrorMsg = null;
        // S1 correctness-only Tail runs only after a fully completed measured pass.
        if (!stoppedByError && _operation == "S1")
        {
            (string tailStatus, string? tailErr) = RunTail(_workload.Probes.Where(p => p.Op == "S1" && !p.Measured), expected);
            if (tailStatus == ServingStatuses.Error)
            {
                tailError = true;
                tailErrorMsg = tailErr;
            }
            if (tailStatus == ServingStatuses.Invalid) sawInvalid = true;
        }

        if (stoppedByError || tailError)
        {
            return new ServingTimingExecution
            {
                Operation = _operation,
                Repetition = _repetition,
                Correctness = ServingStatuses.Error,
                Samples = samples,
                TimedPassWallSeconds = passWall,
                ErrorCategory = stoppedByError ? "timed-probe" : "tail",
                ErrorMessage = stoppedByError ? timedError : tailErrorMsg,
            };
        }

        return new ServingTimingExecution
        {
            Operation = _operation,
            Repetition = _repetition,
            Correctness = sawInvalid ? ServingStatuses.Invalid : ServingStatuses.Valid,
            Samples = samples,
            TimedPassWallSeconds = passWall,
        };
    }

    private (string Status, string? Error) RunWarmup(IReadOnlyList<ServingProbe> measured, IReadOnlyDictionary<(string, long), ServingExpected> expected)
    {
        bool invalid = false;
        foreach (var probe in measured)
        {
            var exp = expected[(probe.Op, probe.Seq)];
            try
            {
                var (cardinality, digest) = Canonicalize(probe, Materialize(probe));
                if (cardinality != exp.Cardinality || digest != exp.Digest) invalid = true;
            }
            catch (Exception ex)
            {
                return (ServingStatuses.Error, ex.Message);
            }
        }
        return (invalid ? ServingStatuses.Invalid : ServingStatuses.Valid, null);
    }

    private (string Status, string? Error) RunTail(IEnumerable<ServingProbe> tails, IReadOnlyDictionary<(string, long), ServingExpected> expected)
    {
        bool invalid = false;
        foreach (var probe in tails)
        {
            var exp = expected[(probe.Op, probe.Seq)];
            try
            {
                var (cardinality, digest) = Canonicalize(probe, Materialize(probe));
                if (cardinality != exp.Cardinality || digest != exp.Digest) invalid = true;
            }
            catch (Exception ex)
            {
                return (ServingStatuses.Error, ex.Message);
            }
        }
        return (invalid ? ServingStatuses.Invalid : ServingStatuses.Valid, null);
    }

    /// <summary>
    /// Candidate retrieval (timed). Serving candidate methods already return
    /// fully materialized logical results; NO timed copy or enumeration is added.
    /// </summary>
    private object Materialize(ServingProbe probe)
    {
        switch (probe.Op)
        {
            case "S1":
                return _candidate.GetConcept(probe.Qid!.Value);
            case "S2":
                return _candidate.LookupLexical(probe.Lang!, probe.Value!);
            case "S3":
                return _candidate.GetLexicalByQid(probe.Qid!.Value);
            case "S4":
                return _candidate.GetInstanceOf(probe.Qid!.Value);
            case "S5":
                return _candidate.GetSubclassOf(probe.Qid!.Value);
            default:
                throw new InvalidOperationException($"unsupported serving op {probe.Op}");
        }
    }

    /// <summary>Canonicalization/digest over an already-materialized result (untimed).</summary>
    private static (long Cardinality, string Digest) Canonicalize(ServingProbe probe, object materialized)
    {
        switch (probe.Op)
        {
            case "S1":
            {
                var hit = (ConceptHit)materialized;
                return (hit.Present ? 1 : 0,
                    Mimir.Catalog.Workload.WorkloadOracle.ConceptResultDigest(probe.Qid!.Value, hit.Present, hit.InT1, hit.InT2));
            }
            case "S2":
            {
                var members = (IReadOnlyList<LexicalHit>)materialized;
                bool isMiss = probe.Stratum == "Miss";
                string digest = members.Count == 0 && isMiss
                    ? Mimir.Catalog.Workload.WorkloadOracle.LexMissDigest(probe.Lang!, probe.Value!)
                    : Mimir.Catalog.Workload.WorkloadOracle.LexMembersDigest(members.Select(m => (m.Qid, m.LexKind)).ToList());
                return (members.Count, digest);
            }
            case "S3":
            {
                var rows = (IReadOnlyList<LexicalRow>)materialized;
                if (rows.Any(r => r.Qid != probe.Qid!.Value))
                    return (rows.Count, "<invalid-qid>");
                return (rows.Count, Mimir.Catalog.Workload.WorkloadOracle.LexicalRowsDigest(
                    probe.Qid.Value, rows.Select(r => (r.Lang, r.LexKind, r.Value)).ToList()));
            }
            case "S4":
            case "S5":
            {
                var targets = ((IReadOnlyList<long>)materialized).OrderBy(t => t).ToArray();
                return (targets.Length, Mimir.Catalog.Workload.WorkloadOracle.AdjacencyDigest(targets));
            }
            default:
                throw new InvalidOperationException($"unsupported serving op {probe.Op}");
        }
    }
}

using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral analytical timing/repetition harness.
///
/// Per repetition 1..3: create a fresh candidate via the factory, Open() once
/// (outside any timer), run one complete untimed warmup A1->A5 pass, run one
/// complete timed A1->A5 pass with post-timer correctness classification per
/// operation, then Dispose(). No candidate is reused across repetitions.
/// Timer boundaries follow the frozen contracts: A1 wraps executor/fold;
/// A2-A5 wrap complete candidate materialization only; canonicalization/digest
/// and expected comparison happen after StopSeconds(). WorkloadMetrics.Median
/// is reused for the authoritative per-operation median of three VALID samples.
/// </summary>
public sealed class AnalyticalTimingRunner
{
    private static readonly string[] Order =
        ["A1-Concept", "A1-LexicalEntry", "A1-InstanceOf", "A1-SubclassOf", "A2", "A3", "A4", "A5"];

    private readonly Func<IAnalyticalCandidate> _factory;
    private readonly AnalyticalWorkload _workload;
    private readonly Func<ITimer> _timerFactory;

    public AnalyticalTimingRunner(Func<IAnalyticalCandidate> factory, AnalyticalWorkload workload, Func<ITimer>? timerFactory = null)
    {
        _factory = factory;
        _workload = workload;
        _timerFactory = timerFactory ?? (() => new StopwatchTimer());
    }

    public AnalyticalTimingResults Run(int repetitions = 3)
    {
        var samples = new List<AnalyticalTimedSample>();
        var warmupFailures = new List<AnalyticalWarmupFailure>();

        for (int rep = 1; rep <= repetitions; rep++)
        {
            IAnalyticalCandidate? candidate = null;
            try
            {
                candidate = _factory();
                candidate.Open();

                var warmupFailure = RunWarmup(candidate);
                if (warmupFailure is { } wf)
                {
                    warmupFailures.Add(new AnalyticalWarmupFailure
                    {
                        Repetition = rep,
                        Operation = wf.Op,
                        ErrorMessage = wf.Error,
                    });
                    continue; // no timed pass on an invalid candidate state
                }

                foreach (var op in Order)
                    samples.Add(MeasureAndClassify(candidate, op, rep));
            }
            catch (Exception ex)
            {
                warmupFailures.Add(new AnalyticalWarmupFailure
                {
                    Repetition = rep,
                    Operation = "Open/create",
                    ErrorMessage = ex.Message,
                });
            }
            finally
            {
                candidate?.Dispose();
            }
        }

        return new AnalyticalTimingResults
        {
            Samples = samples,
            WarmupFailures = warmupFailures,
            Summaries = Summarize(samples),
        };
    }

    private (string Op, string Error)? RunWarmup(IAnalyticalCandidate candidate)
    {
        var a1 = new A1OperationExecutor(candidate);
        string current = "";
        try
        {
            foreach (var op in Order)
            {
                current = op;
                switch (op)
                {
                    case "A1-Concept" or "A1-LexicalEntry" or "A1-InstanceOf" or "A1-SubclassOf":
                        a1.Execute(op);
                        break;
                    case "A2":
                        candidate.A2LangKindCounts();
                        break;
                    case "A3":
                        candidate.A3P31Fanout();
                        break;
                    case "A4":
                        candidate.A4P279Fanout();
                        break;
                    case "A5":
                        candidate.A5P31TargetLabels();
                        break;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            return (current, ex.Message);
        }
    }

    private AnalyticalTimedSample MeasureAndClassify(IAnalyticalCandidate candidate, string operation, int rep)
    {
        var timer = _timerFactory();
        timer.Start();
        long? cardinality = null;
        string? digest = null;
        string? error = null;
        TimedResultStatus status;
        try
        {
            switch (operation)
            {
                case "A1-Concept" or "A1-LexicalEntry" or "A1-InstanceOf" or "A1-SubclassOf":
                {
                    var result = new A1OperationExecutor(candidate).Execute(operation);
                    cardinality = result.ActualRowCount;
                    digest = result.ActualDigest;
                    break;
                }
                case "A2":
                {
                    var rows = new A2A4OperationExecutor(candidate).ExecuteA2();
                    (cardinality, digest) = CanonA2(rows);
                    break;
                }
                case "A3":
                {
                    var rows = new A2A4OperationExecutor(candidate).ExecuteA3();
                    (cardinality, digest) = CanonTargets(rows);
                    break;
                }
                case "A4":
                {
                    var rows = new A2A4OperationExecutor(candidate).ExecuteA4();
                    (cardinality, digest) = CanonTargets(rows);
                    break;
                }
                case "A5":
                {
                    var rows = candidate.A5P31TargetLabels();
                    (cardinality, digest) = CanonA5(rows);
                    break;
                }
                default:
                    throw new InvalidOperationException($"unsupported analytical operation {operation}");
            }
            status = Classify(operation, cardinality!.Value, digest!);
        }
        catch (Exception ex)
        {
            status = TimedResultStatus.Error;
            error = ex.Message;
        }
        double wall = timer.StopSeconds();

        return new AnalyticalTimedSample
        {
            Operation = operation,
            Repetition = rep,
            WallSeconds = wall,
            Status = status,
            ResultCardinality = cardinality,
            ResultDigest = digest,
            ErrorMessage = error,
        };
    }

    private TimedResultStatus Classify(string operation, long cardinality, string digest)
    {
        var expected = _workload.Expected[operation];
        bool valid = cardinality == expected.Cardinality && digest == expected.Digest;
        return valid ? TimedResultStatus.Valid : TimedResultStatus.Invalid;
    }

    private static (long Count, string Digest) CanonA2(IReadOnlyList<(string Lang, string LexKind, long Count)> rows)
    {
        var sorted = rows
            .OrderBy(r => r.Lang, StringComparer.Ordinal)
            .ThenBy(r => r.LexKind, StringComparer.Ordinal)
            .Select(r => WorkloadOracle.LangKindCountRow(r.Lang, r.LexKind, r.Count))
            .ToArray();
        return (rows.Count, WorkloadOracle.AnalyticalRowsDigest(sorted));
    }

    private static (long Count, string Digest) CanonTargets(IReadOnlyList<(long TargetQid, long Count)> rows)
    {
        var sorted = rows.OrderBy(r => r.TargetQid)
            .Select(r => WorkloadOracle.TargetCountRow(r.TargetQid, r.Count))
            .ToArray();
        return (rows.Count, WorkloadOracle.AnalyticalRowsDigest(sorted));
    }

    private static (long Count, string Digest) CanonA5(IReadOnlyList<A5Row> rows)
    {
        var sorted = rows.OrderBy(r => r.TargetQid)
            .Select(r => WorkloadOracle.A5Row(r.TargetQid, r.Fanout, r.EnLabel, r.NbLabel))
            .ToArray();
        return (rows.Count, WorkloadOracle.AnalyticalRowsDigest(sorted));
    }

    private static IReadOnlyList<AnalyticalOpSummary> Summarize(IReadOnlyList<AnalyticalTimedSample> samples)
    {
        var summaries = new List<AnalyticalOpSummary>();
        foreach (var op in Order)
        {
            var opSamples = samples.Where(s => s.Operation == op).ToList();
            if (opSamples.Count == 3 && opSamples.All(s => s.Status == TimedResultStatus.Valid))
            {
                summaries.Add(new AnalyticalOpSummary
                {
                    Operation = op,
                    Status = AnalyticalSummaryStatus.Valid,
                    MedianSeconds = WorkloadMetrics.MedianOfSummaries(opSamples.Select(s => s.WallSeconds).ToList()),
                });
            }
            else
            {
                summaries.Add(new AnalyticalOpSummary { Operation = op, Status = AnalyticalSummaryStatus.Incomplete, MedianSeconds = null });
            }
        }
        return summaries;
    }
}

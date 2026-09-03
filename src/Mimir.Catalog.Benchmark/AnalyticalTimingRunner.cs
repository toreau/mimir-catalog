using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral analytical timing/repetition harness.
///
/// Per repetition 1..3: create a fresh candidate via the factory, Open() once
/// (outside any timer), run one complete untimed warmup A1->A5 pass, run one
/// complete timed A1->A5 pass, then Dispose(). No candidate is reused across
/// repetitions.
///
/// Timing boundaries are frozen: A1 wraps only A1OperationExecutor.Execute
/// (scan + row decode + canonical encoding + MultisetFoldV1 accumulation +
/// fold finalization). A2-A5 wrap only the candidate/executor grouped query and
/// complete result materialization. Timer stop happens immediately after the
/// measured call returns; all canonicalization, encoding, digesting and
/// expected comparison run strictly AFTER StopSeconds(). A measured exception
/// still stops the timer and records an ERROR sample with retained wall time.
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

    /// <summary>Runs the frozen protocol: exactly three analytical repetitions.</summary>
    public AnalyticalTimingResults Run() => RunCore(3);

    private AnalyticalTimingResults RunCore(int repetitions)
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
        var (wall, raw, execError) = Measure(operation, candidate);
        if (execError is not null)
        {
            return new AnalyticalTimedSample
            {
                Operation = operation,
                Repetition = rep,
                WallSeconds = wall,
                Status = TimedResultStatus.Error,
                ErrorMessage = execError.Message,
            };
        }

        // Post-timer classification only. Failures here keep the captured wall
        // time and classify as ERROR; timing is never restarted.
        try
        {
            (long cardinality, string digest) = Canonicalize(operation, raw);
            var expected = _workload.Expected[operation];
            bool valid = cardinality == expected.Cardinality && digest == expected.Digest;
            return new AnalyticalTimedSample
            {
                Operation = operation,
                Repetition = rep,
                WallSeconds = wall,
                Status = valid ? TimedResultStatus.Valid : TimedResultStatus.Invalid,
                ResultCardinality = cardinality,
                ResultDigest = digest,
            };
        }
        catch (Exception ex)
        {
            return new AnalyticalTimedSample
            {
                Operation = operation,
                Repetition = rep,
                WallSeconds = wall,
                Status = TimedResultStatus.Error,
                ErrorMessage = ex.Message,
            };
        }
    }

    /// <summary>Starts a timer, runs only the frozen measured operation, stops the timer.</summary>
    private (double Wall, object? Raw, Exception? Error) Measure(string operation, IAnalyticalCandidate candidate)
    {
        var timer = _timerFactory();
        timer.Start();
        object? raw = null;
        Exception? error = null;
        try
        {
            switch (operation)
            {
                case "A1-Concept" or "A1-LexicalEntry" or "A1-InstanceOf" or "A1-SubclassOf":
                    raw = new A1OperationExecutor(candidate).Execute(operation);
                    break;
                case "A2":
                    raw = new A2A4OperationExecutor(candidate).ExecuteA2();
                    break;
                case "A3":
                    raw = new A2A4OperationExecutor(candidate).ExecuteA3();
                    break;
                case "A4":
                    raw = new A2A4OperationExecutor(candidate).ExecuteA4();
                    break;
                case "A5":
                    raw = candidate.A5P31TargetLabels();
                    break;
                default:
                    throw new InvalidOperationException($"unsupported analytical operation {operation}");
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            // The measured call has returned (or thrown); the timer must stop
            // before any canonicalization runs.
        }
        double wall = timer.StopSeconds();
        return (wall, raw, error);
    }

    private static (long Count, string Digest) Canonicalize(string operation, object raw)
    {
        switch (operation)
        {
            case "A1-Concept" or "A1-LexicalEntry" or "A1-InstanceOf" or "A1-SubclassOf":
            {
                var r = (A1ExecutionResult)raw;
                return (r.ActualRowCount, r.ActualDigest);
            }
            case "A2":
                return CanonA2((IReadOnlyList<(string Lang, string LexKind, long Count)>)raw);
            case "A3":
            case "A4":
                return CanonTargets((IReadOnlyList<(long TargetQid, long Count)>)raw);
            case "A5":
                return CanonA5((IReadOnlyList<A5Row>)raw);
            default:
                throw new InvalidOperationException($"unsupported analytical operation {operation}");
        }
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

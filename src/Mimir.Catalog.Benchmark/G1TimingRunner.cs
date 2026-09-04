using System.Diagnostics;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral G1 child timing runner.
///
/// Per child repetition: fresh candidate owned by the caller; one complete
/// untimed warmup pass over the exact G1 probe sequence with full correctness
/// checking, then one complete timed pass over the same sequence. The timer
/// wraps exactly G1CorrectnessRunner.Traverse (including adjacency
/// materialization/sorting and graph traversal); cardinality/visited/digest and
/// expected comparison happen after StopSeconds. Samples keep every actual
/// start wall (including >= 30.0 s); the child never emits TIMEOUT.
/// </summary>
public sealed class G1TimingRunner
{
    private readonly G1CorrectnessRunner _runner;
    private readonly GraphWorkload _workload;
    private readonly int _repetition;
    private readonly Func<ITimer> _timerFactory;

    public G1TimingRunner(
        IStorageCandidate candidate,
        GraphWorkload workload,
        int repetition,
        Func<ITimer>? timerFactory = null)
    {
        _runner = new G1CorrectnessRunner(candidate);
        _workload = workload;
        _repetition = repetition;
        _timerFactory = timerFactory ?? (() => new StopwatchTimer());
    }

    public G1TimingExecution Execute()
    {
        var probes = _workload.Probes;
        var expected = _workload.Expected;

        // Warmup: complete untimed correctness pass over the exact sequence.
        string warmupStatus = RunWarmup(probes, expected, out string? warmupError);
        if (warmupStatus != ServingStatuses.Valid)
        {
            return new G1TimingExecution
            {
                Repetition = _repetition,
                Correctness = warmupStatus,
                Samples = Array.Empty<G1TimedSample>(),
                TimedPassWallSeconds = null,
                ErrorCategory = warmupError is not null ? "warmup" : null,
                ErrorMessage = warmupError,
            };
        }

        // Timed pass over the exact same sequence.
        var samples = new List<G1TimedSample>();
        bool sawInvalid = false;
        string? timedError = null;
        var passClock = Stopwatch.StartNew();
        foreach (var probe in probes)
        {
            var exp = expected[("G1", probe.Seq)];
            var timer = _timerFactory();
            timer.Start();
            string? traverseError = null;
            GraphTraversal.Result? traversal = null;
            try
            {
                traversal = _runner.Traverse(probe);
            }
            catch (Exception ex)
            {
                traverseError = ex.Message;
            }
            double wall = timer.StopSeconds();

            if (traverseError is not null)
            {
                samples.Add(new G1TimedSample("G1", probe.Seq, probe.Stratum, wall,
                    ServingStatuses.Error, Error: traverseError));
                timedError = traverseError;
                break;
            }

            string status;
            long cardinality = traversal!.Discovered.Length;
            long visited = traversal.VisitedCount;
            string? digest = null;
            string? error = null;
            if (traversal.ExceededGuard)
            {
                status = ServingStatuses.Error;
                error = "guard exceeded (5000) during G1 execution";
            }
            else
            {
                string actualDigest;
                try
                {
                    actualDigest = WorkloadOracle.G1Digest(traversal.Discovered, traversal.VisitedCount);
                }
                catch (Exception ex)
                {
                    samples.Add(new G1TimedSample("G1", probe.Seq, probe.Stratum, wall,
                        ServingStatuses.Error, Error: ex.Message));
                    timedError = ex.Message;
                    break;
                }
                bool valid = cardinality == exp.Cardinality
                    && visited == exp.Visited
                    && actualDigest == exp.Digest;
                status = valid ? ServingStatuses.Valid : ServingStatuses.Invalid;
                digest = actualDigest;
                if (!valid) sawInvalid = true;
            }

            samples.Add(new G1TimedSample("G1", probe.Seq, probe.Stratum, wall,
                status, error is null ? cardinality : null, error is null ? visited : null, digest, error));
            if (status == ServingStatuses.Error)
            {
                timedError = error;
                break;
            }
        }
        passClock.Stop();
        double? passWall = probes.Count > 0 ? passClock.Elapsed.TotalSeconds : null;

        if (timedError is not null)
        {
            return new G1TimingExecution
            {
                Repetition = _repetition,
                Correctness = ServingStatuses.Error,
                Samples = samples,
                TimedPassWallSeconds = passWall,
                ErrorCategory = "timed-start",
                ErrorMessage = timedError,
            };
        }

        return new G1TimingExecution
        {
            Repetition = _repetition,
            Correctness = sawInvalid ? ServingStatuses.Invalid : ServingStatuses.Valid,
            Samples = samples,
            TimedPassWallSeconds = passWall,
        };
    }

    private string RunWarmup(IReadOnlyList<GraphProbe> probes, IReadOnlyDictionary<(string, long), GraphExpected> expected, out string? error)
    {
        bool invalid = false;
        error = null;
        foreach (var probe in probes)
        {
            var exp = expected[("G1", probe.Seq)];
            GraphTraversal.Result traversal;
            try
            {
                traversal = _runner.Traverse(probe);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return ServingStatuses.Error;
            }
            if (traversal.ExceededGuard)
            {
                error = "guard exceeded (5000) during G1 warmup";
                return ServingStatuses.Error;
            }
            string digest;
            try
            {
                digest = WorkloadOracle.G1Digest(traversal.Discovered, traversal.VisitedCount);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return ServingStatuses.Error;
            }
            bool valid = traversal.Discovered.Length == exp.Cardinality
                && traversal.VisitedCount == exp.Visited
                && digest == exp.Digest;
            if (!valid) invalid = true;
        }
        return invalid ? ServingStatuses.Invalid : ServingStatuses.Valid;
    }
}

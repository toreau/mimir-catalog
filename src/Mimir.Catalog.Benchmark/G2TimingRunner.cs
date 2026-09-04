namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral G2 child timing runner.
///
/// Per child repetition: one complete untimed 200-concept warmup batch with full
/// correctness (via G2CorrectnessRunner.Classify). Only when the warmup Batch is
/// VALID does one complete timed batch run; the timer wraps exactly
/// G2OperationExecutor.Execute, and all digest/comparison work happens after
/// StopSeconds through the existing public Classify seam. The child never emits
/// TIMEOUT; a raw timed batch keeps its actual wall (including >= 120.0 s).
/// </summary>
public sealed class G2TimingRunner
{
    private readonly IStorageCandidate _candidate;
    private readonly G2Workload _workload;
    private readonly int _repetition;
    private readonly Func<ITimer> _timerFactory;

    public G2TimingRunner(
        IStorageCandidate candidate,
        G2Workload workload,
        int repetition,
        Func<ITimer>? timerFactory = null)
    {
        _candidate = candidate;
        _workload = workload;
        _repetition = repetition;
        _timerFactory = timerFactory ?? (() => new StopwatchTimer());
    }

    public G2TimingExecution Execute()
    {
        var executor = new G2OperationExecutor(_candidate);
        var classify = new G2CorrectnessRunner(_candidate);

        // Warmup: complete untimed full batch, correctness outside any timer.
        var warmupOutcomes = executor.Execute(_workload.Concepts);
        var (_, warmupBatch) = classify.Classify(_workload, warmupOutcomes);

        if (warmupBatch.Status != ServingStatuses.Valid)
        {
            return new G2TimingExecution
            {
                Repetition = _repetition,
                Correctness = warmupBatch.Status,
                PerInputResults = Array.Empty<G2TimedPerInputResult>(),
                BatchResult = null,
                ErrorCategory = warmupBatch.Status == ServingStatuses.Error ? "warmup" : null,
                ErrorMessage = warmupBatch.Status == ServingStatuses.Error ? warmupBatch.ErrorMessage : null,
            };
        }

        var timer = _timerFactory();
        timer.Start();
        IReadOnlyList<G2PerInputExecutionOutcome> timedOutcomes;
        try
        {
            timedOutcomes = executor.Execute(_workload.Concepts);
        }
        catch (Exception ex)
        {
            return new G2TimingExecution
            {
                Repetition = _repetition,
                Correctness = ServingStatuses.Error,
                PerInputResults = Array.Empty<G2TimedPerInputResult>(),
                BatchResult = null,
                ErrorCategory = "runtime",
                ErrorMessage = ex.Message,
            };
        }
        double timedWall = timer.StopSeconds();

        var (perInput, batch) = classify.Classify(_workload, timedOutcomes);

        var rawPerInput = perInput.Select(p => new G2TimedPerInputResult(
            p.Item, p.Qid, p.SourceStratum,
            p.Status,
            p.Status == ServingStatuses.Error ? null : p.ActualCardinality,
            p.Status == ServingStatuses.Error ? null : p.ActualDigest,
            p.ErrorMessage)).ToList();

        string correctness = batch.Status;
        return new G2TimingExecution
        {
            Repetition = _repetition,
            Correctness = correctness,
            PerInputResults = rawPerInput,
            BatchResult = new G2TimedBatchResult(
                timedWall,
                batch.Status,
                batch.Status == ServingStatuses.Error ? null : batch.ActualCardinality,
                batch.Status == ServingStatuses.Error ? null : batch.ActualDigest,
                batch.ErrorMessage),
            ErrorCategory = correctness == ServingStatuses.Error ? "timed-batch" : null,
            ErrorMessage = correctness == ServingStatuses.Error ? batch.ErrorMessage : null,
        };
    }
}

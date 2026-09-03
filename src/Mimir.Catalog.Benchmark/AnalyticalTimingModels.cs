namespace Mimir.Catalog.Benchmark;

public enum TimedResultStatus { Valid, Invalid, Timeout, Error }

public enum AnalyticalSummaryStatus { Valid, Incomplete }

/// <summary>A single attempted timed analytical operation sample (1..3).</summary>
public sealed class AnalyticalTimedSample
{
    public required string Operation { get; init; }
    public required int Repetition { get; init; }
    public required double WallSeconds { get; init; }
    public required TimedResultStatus Status { get; init; }
    public long? ResultCardinality { get; init; }
    public string? ResultDigest { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Explicit warmup failure; that repetition produces no timed samples.</summary>
public sealed class AnalyticalWarmupFailure
{
    public required int Repetition { get; init; }
    public required string Operation { get; init; }
    public required string ErrorMessage { get; init; }
}

public sealed class AnalyticalOpSummary
{
    public required string Operation { get; init; }
    public required AnalyticalSummaryStatus Status { get; init; }
    public double? MedianSeconds { get; init; }
}

public sealed class AnalyticalTimingResults
{
    public required IReadOnlyList<AnalyticalTimedSample> Samples { get; init; }
    public required IReadOnlyList<AnalyticalOpSummary> Summaries { get; init; }
    public required IReadOnlyList<AnalyticalWarmupFailure> WarmupFailures { get; init; }
}

/// <summary>Injective monotonic stopwatch surface; prod uses Stopwatch.</summary>
public interface ITimer
{
    void Start();
    /// <summary>Stops and returns elapsed wall time in unrounded double seconds.</summary>
    double StopSeconds();
}

public sealed class StopwatchTimer : ITimer
{
    private readonly System.Diagnostics.Stopwatch _sw = new();
    public void Start() { _sw.Restart(); }
    public double StopSeconds() { _sw.Stop(); return _sw.Elapsed.TotalSeconds; }
}

/// <summary>Deterministic test timer: each StopSeconds() pops the next scripted duration.</summary>
public sealed class ScriptedTimer : ITimer
{
    private readonly Queue<double> _durations;
    private bool _running;

    public ScriptedTimer(IEnumerable<double> durations) => _durations = new Queue<double>(durations);

    public void Start() => _running = true;
    public double StopSeconds()
    {
        if (!_running) throw new InvalidOperationException("not started");
        _running = false;
        if (_durations.Count == 0) throw new InvalidOperationException("no scripted duration left");
        return _durations.Dequeue();
    }
}

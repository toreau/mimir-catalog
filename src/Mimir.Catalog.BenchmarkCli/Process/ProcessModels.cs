namespace Mimir.Catalog.BenchmarkCli.Process;

/// <summary>Closed parent-level outcome domain (resource measurement joins in 4d.1b.2).</summary>
public enum ProcessOutcome
{
    CompletedProtocolResult,
    Timeout,
    ProcessStartError,
    ProcessCrashOrNonzeroExit,
    ProtocolResultError,
    ParentError,
}

/// <summary>Low-level process invocation; outer executable may change in 4d.1b.2.</summary>
public sealed class ProcessInvocation
{
    public required string Executable { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }

    public static ProcessInvocation BenchmarkChild(string childExecutable, string requestPath)
        => new() { Executable = childExecutable, Arguments = new[] { "child", "--request", requestPath } };
}

/// <summary>Parent-owned result of one child invocation. Never implies descendant termination.</summary>
public sealed class ChildProcessResult
{
    public required ProcessOutcome Outcome { get; init; }
    public bool TimedOut { get; init; }
    public int? ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public double ElapsedParentWallSeconds { get; init; }

    public bool KillAttempted { get; init; }
    public bool KillCallSucceeded { get; init; }
    public string? KillError { get; init; }
    public bool WrapperExitObserved { get; init; }
    public bool DescendantTerminationVerified { get; init; }

    public bool OutputDrainCompleted { get; init; }
    public string? CleanupError { get; init; }

    public Mimir.Catalog.BenchmarkCli.Protocol.ChildResultEnvelope? ParsedChildResult { get; init; }
    public string? ValidationError { get; init; }
}

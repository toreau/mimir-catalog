using System.Diagnostics;
using System.Text;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Process;

/// <summary>
/// Parent-owned runner for exactly one child invocation: safe start, concurrent
/// stdout/stderr drains, parent wall timing, external timeout, bounded cleanup
/// and strict protocol parsing/correlation. Never claims descendant termination.
/// </summary>
public static class ChildProcessRunner
{
    /// <summary>Infrastructure cleanup/drain grace; not a benchmark timeout.</summary>
    public static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(5);

    public static Task<ChildProcessResult> RunAsync(ProcessInvocation invocation, TimeSpan timeout, ChildRequestEnvelope request)
        => RunCoreAsync(invocation, timeout, request, process => process.Kill(entireProcessTree: true));

    /// <summary>Test-only seam: injects the kill action so kill success/failure is deterministic.</summary>
    internal static Task<ChildProcessResult> RunAsyncForTest(ProcessInvocation invocation, TimeSpan timeout, ChildRequestEnvelope request, Action<System.Diagnostics.Process> killAction)
        => RunCoreAsync(invocation, timeout, request, killAction);

    private static async Task<ChildProcessResult> RunCoreAsync(ProcessInvocation invocation, TimeSpan timeout, ChildRequestEnvelope request, Action<System.Diagnostics.Process> killAction)
    {
        var sw = Stopwatch.StartNew();
        using var process = new System.Diagnostics.Process();
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.FileName = invocation.Executable;
        foreach (var arg in invocation.Arguments)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ChildProcessResult
            {
                Outcome = ProcessOutcome.ProcessStartError,
                ExitCode = null,
                ElapsedParentWallSeconds = sw.Elapsed.TotalSeconds,
                OutputDrainCompleted = false,
                ValidationError = ex.Message,
            };
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation only signals the deadline; it does not terminate the process.
            }

            if (!process.HasExited)
            {
                // Commit timeout.
                string? killError = null;
                bool killSucceeded = false;
                try
                {
                    killAction(process);
                    killSucceeded = true;
                }
                catch (Exception kex)
                {
                    killError = kex.Message;
                }

                // One single cleanup budget: wrapper-exit observation and output
                // drains all share CleanupGrace. No per-phase grace restarts.
                var cleanup = await TimeoutCleanupAsync(process, stdoutTask, stderrTask, CleanupGrace).ConfigureAwait(false);

                return new ChildProcessResult
                {
                    Outcome = ProcessOutcome.Timeout,
                    TimedOut = true,
                    ExitCode = process.HasExited ? process.ExitCode : null,
                    Stdout = cleanup.DrainCompleted ? await stdoutTask.ConfigureAwait(false) : "",
                    Stderr = cleanup.DrainCompleted ? await stderrTask.ConfigureAwait(false) : "",
                    ElapsedParentWallSeconds = sw.Elapsed.TotalSeconds,
                    KillAttempted = true,
                    KillCallSucceeded = killSucceeded,
                    KillError = killError,
                    WrapperExitObserved = cleanup.WrapperExitObserved,
                    DescendantTerminationVerified = false,
                    OutputDrainCompleted = cleanup.DrainCompleted,
                    CleanupError = cleanup.Error,
                    ValidationError = cleanup.Error ?? (cleanup.DrainCompleted ? null : "timeout cleanup grace expired"),
                };
            }

            // Normal exit: complete drains before parsing.
            (bool normalDrain, string? normalCleanupError) = await DrainWithinGrace(stdoutTask, stderrTask).ConfigureAwait(false);
            if (!normalDrain)
            {
                return new ChildProcessResult
                {
                    Outcome = ProcessOutcome.ParentError,
                    ExitCode = process.ExitCode,
                    ElapsedParentWallSeconds = sw.Elapsed.TotalSeconds,
                    WrapperExitObserved = true,
                    DescendantTerminationVerified = false,
                    OutputDrainCompleted = false,
                    CleanupError = normalCleanupError ?? "normal-exit drain grace expired",
                    ValidationError = "output could not be fully drained on normal exit",
                };
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            int exitCode = process.ExitCode;

            if (exitCode != 0)
            {
                // Nonzero exit: stdout is never trusted as a child result.
                return new ChildProcessResult
                {
                    Outcome = ProcessOutcome.ProcessCrashOrNonzeroExit,
                    ExitCode = exitCode,
                    Stdout = stdout,
                    Stderr = stderr,
                    ElapsedParentWallSeconds = sw.Elapsed.TotalSeconds,
                    WrapperExitObserved = true,
                    DescendantTerminationVerified = false,
                    OutputDrainCompleted = true,
                    ValidationError = $"nonzero process exit {exitCode}",
                };
            }

            // exit 0: strict parse + correlation.
            ChildResultEnvelope parsed;
            try
            {
                parsed = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(Encoding.UTF8.GetBytes(stdout));
            }
            catch (Exception ex)
            {
                return new ChildProcessResult
                {
                    Outcome = ProcessOutcome.ProtocolResultError,
                    ExitCode = exitCode,
                    Stdout = stdout,
                    Stderr = stderr,
                    ElapsedParentWallSeconds = sw.Elapsed.TotalSeconds,
                    WrapperExitObserved = true,
                    DescendantTerminationVerified = false,
                    OutputDrainCompleted = true,
                    ValidationError = $"child result parse failure: {ex.Message}",
                };
            }

            string? mismatch = Correlate(request, parsed);
            if (mismatch is not null)
            {
                return new ChildProcessResult
                {
                    Outcome = ProcessOutcome.ProtocolResultError,
                    ExitCode = exitCode,
                    Stdout = stdout,
                    Stderr = stderr,
                    ElapsedParentWallSeconds = sw.Elapsed.TotalSeconds,
                    WrapperExitObserved = true,
                    DescendantTerminationVerified = false,
                    OutputDrainCompleted = true,
                    ParsedChildResult = parsed,
                    ValidationError = mismatch,
                };
            }

            return new ChildProcessResult
            {
                Outcome = ProcessOutcome.CompletedProtocolResult,
                ExitCode = exitCode,
                Stdout = stdout,
                Stderr = stderr,
                ElapsedParentWallSeconds = sw.Elapsed.TotalSeconds,
                WrapperExitObserved = true,
                DescendantTerminationVerified = false,
                OutputDrainCompleted = true,
                ParsedChildResult = parsed,
            };
        }
        catch (Exception ex)
        {
            return new ChildProcessResult
            {
                Outcome = ProcessOutcome.ParentError,
                ExitCode = process.HasExited ? process.ExitCode : null,
                ElapsedParentWallSeconds = sw.Elapsed.TotalSeconds,
                WrapperExitObserved = process.HasExited,
                DescendantTerminationVerified = false,
                ValidationError = ex.Message,
            };
        }
    }


    private sealed record TimeoutCleanupState(bool WrapperExitObserved, bool DrainCompleted, string? Error);

    private static async Task<TimeoutCleanupState> TimeoutCleanupAsync(
        System.Diagnostics.Process process,
        Task<string> stdoutTask,
        Task<string> stderrTask,
        TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        var drains = Task.WhenAll(stdoutTask, stderrTask);
        var exitWait = process.WaitForExitAsync();

        // Wrapper-exit observation first, sharing the single budget.
        TimeSpan remaining = budget - clock.Elapsed;
        var exitWinner = await Task.WhenAny(exitWait, Task.Delay(remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining)).ConfigureAwait(false);
        bool wrapperExitObserved = process.HasExited;

        // Output drains within whatever budget remains.
        remaining = budget - clock.Elapsed;
        var drainWinner = await Task.WhenAny(drains, Task.Delay(remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining)).ConfigureAwait(false);
        bool drainCompleted = drainWinner == drains;
        if (drainCompleted)
        {
            try
            {
                await drains.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new TimeoutCleanupState(wrapperExitObserved, false, $"output drain fault: {ex.Message}");
            }
        }

        string? error = drainCompleted
            ? null
            : "cleanup budget expired before output drains completed";
        return new TimeoutCleanupState(wrapperExitObserved, drainCompleted, error);
    }

    private static async Task<(bool Completed, string? Error)> DrainWithinGrace(Task<string> stdoutTask, Task<string> stderrTask)
    {
        var all = Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            var winner = await Task.WhenAny(all, Task.Delay(CleanupGrace)).ConfigureAwait(false);
            if (winner != all)
                return (false, "output drain grace expired");
            await all.ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"output drain fault: {ex.Message}");
        }
    }

    private static string? Correlate(ChildRequestEnvelope request, ChildResultEnvelope result)
    {
        string? Field(string name, string expected, string actual)
            => expected != actual ? $"{name} mismatch" : null;
        var errors = new[]
        {
            Field(nameof(request.ProtocolVersion), request.ProtocolVersion, result.ProtocolVersion),
            Field(nameof(request.CandidateId), request.CandidateId, result.CandidateId),
            Field(nameof(request.CandidateConfigId), request.CandidateConfigId, result.CandidateConfigId),
            Field(nameof(request.WorkloadId), request.WorkloadId, result.WorkloadId),
            Field(nameof(request.CorpusId), request.CorpusId, result.CorpusId),
            Field(nameof(request.WorkloadClass), request.WorkloadClass.ToString(), result.WorkloadClass.ToString()),
            Field(nameof(request.Operation), request.Operation, result.Operation),
            Field(nameof(request.Repetition), request.Repetition.ToString(), result.Repetition.ToString()),
        }.Where(x => x is not null).ToList();
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }
}

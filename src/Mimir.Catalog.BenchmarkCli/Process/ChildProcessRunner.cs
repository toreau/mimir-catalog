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

    public static async Task<ChildProcessResult> RunAsync(ProcessInvocation invocation, TimeSpan timeout, ChildRequestEnvelope request)
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
                    process.Kill(entireProcessTree: true);
                    killSucceeded = true;
                }
                catch (Exception kex)
                {
                    killError = kex.Message;
                }

                var exitTask = process.WaitForExitAsync();
                _ = await Task.WhenAny(exitTask, Task.Delay(CleanupGrace)).ConfigureAwait(false);
                bool wrapperExitObserved = process.HasExited;

                (bool drainCompleted, string? cleanupError) = await DrainWithinGrace(stdoutTask, stderrTask).ConfigureAwait(false);

                return new ChildProcessResult
                {
                    Outcome = ProcessOutcome.Timeout,
                    TimedOut = true,
                    ExitCode = process.HasExited ? process.ExitCode : null,
                    Stdout = drainCompleted ? await stdoutTask.ConfigureAwait(false) : "",
                    Stderr = drainCompleted ? await stderrTask.ConfigureAwait(false) : "",
                    ElapsedParentWallSeconds = sw.Elapsed.TotalSeconds,
                    KillAttempted = true,
                    KillCallSucceeded = killSucceeded,
                    KillError = killError,
                    WrapperExitObserved = wrapperExitObserved,
                    DescendantTerminationVerified = false,
                    OutputDrainCompleted = drainCompleted,
                    CleanupError = cleanupError,
                    ValidationError = cleanupError ?? (drainCompleted ? null : "timeout cleanup grace expired"),
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

    private static async Task<(bool Completed, string? Error)> DrainWithinGrace(Task<string> stdoutTask, Task<string> stderrTask)
    {
        var all = Task.WhenAll(stdoutTask, stderrTask);
        var winner = await Task.WhenAny(all, Task.Delay(CleanupGrace)).ConfigureAwait(false);
        if (winner != all)
            return (false, "output drain grace expired");
        return (true, null);
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

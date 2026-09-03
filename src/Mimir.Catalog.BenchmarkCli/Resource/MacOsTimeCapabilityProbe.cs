using System.Diagnostics;
using System.Text;

namespace Mimir.Catalog.BenchmarkCli.Resource;

public sealed class CapabilityProbeResult
{
    public required bool Supported { get; init; }
    public string? Reason { get; init; }
    public long? RssBytes { get; init; }
}

internal sealed record ProbeExecutionResult(
    bool LaunchSucceeded,
    int ExitCode,
    string Stdout,
    string Stderr,
    string? ResourceText,
    string? LaunchError);

/// <summary>
/// One-time macOS /usr/bin/time capability probe (never per sample).
/// RunCore(isMacOs, executeProbe) keeps the whole control flow deterministic
/// under tests: non-macOS short-circuits before any spawn; launch failure is a
/// thrown/injected failure.
/// </summary>
public static class MacOsTimeCapabilityProbe
{
    public static CapabilityProbeResult Run()
        => RunCore(OperatingSystem.IsMacOS(), RealProbeExecution);

    internal static CapabilityProbeResult RunForTest(bool isMacOs, Func<ProbeExecutionResult> executeProbe)
        => RunCore(isMacOs, executeProbe);

    private static CapabilityProbeResult RunCore(bool isMacOs, Func<ProbeExecutionResult> executeProbe)
    {
        if (!isMacOs)
            return new CapabilityProbeResult { Supported = false, Reason = "not macOS" };

        ProbeExecutionResult result;
        try
        {
            result = executeProbe();
        }
        catch (Exception ex)
        {
            return new CapabilityProbeResult { Supported = false, Reason = $"launch failure: {ex.Message}" };
        }

        if (!result.LaunchSucceeded)
            return new CapabilityProbeResult { Supported = false, Reason = $"launch failure: {result.LaunchError ?? "process could not be launched"}" };
        if (result.ExitCode != 0)
            return new CapabilityProbeResult { Supported = false, Reason = $"probe exit code {result.ExitCode} (stderr: {result.Stderr.Trim()})" };
        if (result.ResourceText is null)
            return new CapabilityProbeResult { Supported = false, Reason = "probe produced no -o output file" };
        if (!MacOsTimeRssParser.TryParse(result.ResourceText, out long bytes, out string? error))
            return new CapabilityProbeResult { Supported = false, Reason = $"probe RSS parse failed: {error}" };
        if (bytes < 0)
            return new CapabilityProbeResult { Supported = false, Reason = "probe RSS negative" };
        return new CapabilityProbeResult { Supported = true, RssBytes = bytes };
    }

    private static ProbeExecutionResult RealProbeExecution()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"mimir-time-probe-{Guid.NewGuid():N}.txt");
        try
        {
            File.Delete(tmp);
            using var p = new System.Diagnostics.Process();
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.FileName = MacOsTimeInvocation.TimeExecutable;
            foreach (var arg in BuildProbeArguments(tmp))
                p.StartInfo.ArgumentList.Add(arg);

            string stdout;
            string stderr;
            try
            {
                p.Start();
                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
            }
            catch (Exception ex)
            {
                return new ProbeExecutionResult(false, -1, "", "", null, ex.Message);
            }

            return new ProbeExecutionResult(
                LaunchSucceeded: true,
                ExitCode: p.ExitCode,
                Stdout: stdout,
                Stderr: stderr,
                ResourceText: File.Exists(tmp) ? File.ReadAllText(tmp) : null,
                LaunchError: null);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Pure invocation shape: /usr/bin/time -l -o &lt;path&gt; /usr/bin/true.</summary>
    internal static string[] BuildProbeArguments(string outputPath)
        => new[] { "-l", "-o", outputPath, "/usr/bin/true" };
}

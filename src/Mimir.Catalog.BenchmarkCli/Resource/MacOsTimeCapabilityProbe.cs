using System.Diagnostics;
using System.Text;
using Mimir.Catalog.BenchmarkCli.Resource;

namespace Mimir.Catalog.BenchmarkCli.Resource;

public sealed class CapabilityProbeResult
{
    public required bool Supported { get; init; }
    public string? Reason { get; init; }
    public long? RssBytes { get; init; }
}

/// <summary>One-time macOS /usr/bin/time capability probe (never per sample).</summary>
public static class MacOsTimeCapabilityProbe
{
    public static CapabilityProbeResult Run()
    {
        if (!OperatingSystem.IsMacOS())
            return new CapabilityProbeResult { Supported = false, Reason = "not macOS" };

        string tmp = Path.Combine(Path.GetTempPath(), $"mimir-time-probe-{Guid.NewGuid():N}.txt");
        try
        {
            File.Delete(tmp);
            using var p = new System.Diagnostics.Process();
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.FileName = MacOsTimeInvocation.TimeExecutable;
            p.StartInfo.ArgumentList.Add("-l");
            p.StartInfo.ArgumentList.Add("-o");
            p.StartInfo.ArgumentList.Add(tmp);
            p.StartInfo.ArgumentList.Add("/usr/bin/true");

            string stdout = "";
            string stderr = "";
            try
            {
                p.Start();
                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
            }
            catch (Exception ex)
            {
                return new CapabilityProbeResult { Supported = false, Reason = $"launch failure: {ex.Message}" };
            }

            return Classify(p.ExitCode, tmp, stdout, stderr);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    internal static CapabilityProbeResult Classify(int exitCode, string outputPath, string stdout, string stderr)
    {
        if (exitCode != 0)
            return new CapabilityProbeResult { Supported = false, Reason = $"probe exit code {exitCode} (stderr: {stderr.Trim()})" };
        if (!File.Exists(outputPath))
            return new CapabilityProbeResult { Supported = false, Reason = "probe produced no -o output file" };
        if (!MacOsTimeRssParser.TryParse(File.ReadAllText(outputPath), out long bytes, out string? error))
            return new CapabilityProbeResult { Supported = false, Reason = $"probe RSS parse failed: {error}" };
        if (bytes < 0)
            return new CapabilityProbeResult { Supported = false, Reason = "probe RSS negative" };
        return new CapabilityProbeResult { Supported = true, RssBytes = bytes };
    }
}

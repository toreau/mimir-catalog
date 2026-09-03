using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Resource;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class ResourceInvocationTests
{
    [Fact]
    public void Wrapper_PreservesExactShapeAndLiterals()
    {
        var inner = new ProcessInvocation
        {
            Executable = "/tmp/bench child.exe",
            Arguments = new[] { "child", "--request", "/tmp/a path/request;&|$.json" },
        };
        var wrapped = MacOsTimeInvocation.Wrap(inner, "/tmp/resource file;&|$.txt");
        Assert.Equal("/usr/bin/time", wrapped.Executable);
        Assert.Equal(7, wrapped.Arguments.Count);
        Assert.Equal(new[] { "-l", "-o", "/tmp/resource file;&|$.txt", "/tmp/bench child.exe",
            "child", "--request", "/tmp/a path/request;&|$.json" }, wrapped.Arguments);
        Assert.DoesNotContain("-a", wrapped.Arguments);
    }
}

public class RssParserTests
{
    [Theory]
    [InlineData("            maximum resident set size", false)] // no numeric token
    [InlineData("", false)]
    [InlineData("   \n  \n", false)]
    [InlineData("0  maximum resident set size\n", true)]
    [InlineData("12345678\tmaximum resident set size", true)]
    [InlineData("9223372036854775807 maximum resident set size", true)]
    [InlineData("9223372036854775808 maximum resident set size", false)]
    [InlineData("-1 maximum resident set size", false)]
    [InlineData("1.5 maximum resident set size", false)]
    [InlineData("1,000 maximum resident set size", false)]
    [InlineData("123 KB maximum resident set size", false)]
    [InlineData("abc maximum resident set size", false)]
    [InlineData("12 maximum resident set size extra", false)]
    [InlineData("12 minimum resident set size", false)]
    [InlineData("12 peak memory footprint", false)]
    [InlineData("1 maximum resident set size\n2 maximum resident set size", false)]
    public void Parser_Deterministic(string text, bool valid)
    {
        Assert.Equal(valid, MacOsTimeRssParser.TryParse(text, out _, out _));
    }

    [Fact]
    public void Parser_RealisticTimeOutput_ExtractsBytes()
    {
        const string output = """
            0.03 real         0.02 user         0.00 sys
            123456789  maximum resident set size
            12345  average shared memory size
            1  page reclaims
            """;
        Assert.True(MacOsTimeRssParser.TryParse(output, out long bytes, out _));
        Assert.Equal(123456789L, bytes);
    }

    [Fact]
    public void Parser_LeadingWhitespaceAndZero_Valid()
    {
        Assert.True(MacOsTimeRssParser.TryParse("   0   maximum resident set size   \n", out long bytes, out _));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void ParseFile_Missing_Throws()
    {
        Assert.Throws<RssParseException>(() => MacOsTimeRssParser.ParseFile("/definitely/not/here.txt"));
    }
}

public class ResourceClassificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-res-" + Guid.NewGuid().ToString("N"));
    public ResourceClassificationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string PathFor(string name) => Path.Combine(_dir, name);
    private string WriteValid(string name)
    {
        string p = PathFor(name);
        File.WriteAllText(p, "987654321  maximum resident set size\n");
        return p;
    }
    private string WriteMalformed(string name)
    {
        string p = PathFor(name);
        File.WriteAllText(p, "12 KB maximum resident set size\n");
        return p;
    }

    private static ChildProcessResult Mk(ProcessOutcome outcome, bool timedOut = false, bool wrapperExit = false) => new()
    {
        Outcome = outcome,
        TimedOut = timedOut,
        WrapperExitObserved = wrapperExit,
        OutputDrainCompleted = true,
    };

    [Theory]
    [InlineData(ProcessOutcome.CompletedProtocolResult)]
    [InlineData(ProcessOutcome.ProcessCrashOrNonzeroExit)]
    [InlineData(ProcessOutcome.ProtocolResultError)]
    [InlineData(ProcessOutcome.ParentError)]
    public void CompletedWrapper_ValidFile_ValidRss(ProcessOutcome outcome)
    {
        string p = WriteValid("v.txt");
        var r = ResourceMeasuredChildRunner.Classify(Mk(outcome), p);
        Assert.Equal(ResourceMeasurementStatus.Valid, r.ResourceStatus);
        Assert.Equal(987654321L, r.ExternalPeakRssBytes);
        Assert.Equal(outcome, r.ProcessResult.Outcome);
    }

    [Theory]
    [InlineData(ProcessOutcome.ProcessCrashOrNonzeroExit)]
    [InlineData(ProcessOutcome.ProtocolResultError)]
    public void CompletedWrapper_MissingFile_Error(ProcessOutcome outcome)
    {
        var r = ResourceMeasuredChildRunner.Classify(Mk(outcome), PathFor("missing.txt"));
        Assert.Equal(ResourceMeasurementStatus.Error, r.ResourceStatus);
        Assert.Null(r.ExternalPeakRssBytes);
        Assert.NotNull(r.ResourceError);
        Assert.Equal(outcome, r.ProcessResult.Outcome);
    }

    [Fact]
    public void CompletedWrapper_MalformedFile_Error()
    {
        string p = WriteMalformed("m.txt");
        var r = ResourceMeasuredChildRunner.Classify(Mk(ProcessOutcome.CompletedProtocolResult), p);
        Assert.Equal(ResourceMeasurementStatus.Error, r.ResourceStatus);
        Assert.Null(r.ExternalPeakRssBytes);
    }

    [Fact]
    public void Timeout_NoWrapperExit_Unavailable_FileNotTrusted()
    {
        string p = WriteValid("valid-but-active.txt"); // looks valid, but wrapper not observed exited
        var r = ResourceMeasuredChildRunner.Classify(Mk(ProcessOutcome.Timeout, timedOut: true, wrapperExit: false), p);
        Assert.Equal(ResourceMeasurementStatus.Unavailable, r.ResourceStatus);
        Assert.Null(r.ExternalPeakRssBytes);
    }

    [Fact]
    public void Timeout_WrapperExitObserved_ValidCompleteFile_Valid()
    {
        string p = WriteValid("after-exit.txt");
        var r = ResourceMeasuredChildRunner.Classify(Mk(ProcessOutcome.Timeout, timedOut: true, wrapperExit: true), p);
        Assert.Equal(ResourceMeasurementStatus.Valid, r.ResourceStatus);
        Assert.Equal(987654321L, r.ExternalPeakRssBytes);
    }

    [Fact]
    public void Timeout_WrapperExitObserved_MalformedFile_Unavailable_NotError()
    {
        string p = WriteMalformed("interrupted.txt");
        var r = ResourceMeasuredChildRunner.Classify(Mk(ProcessOutcome.Timeout, timedOut: true, wrapperExit: true), p);
        Assert.Equal(ResourceMeasurementStatus.Unavailable, r.ResourceStatus);
    }

    [Fact]
    public void Timeout_NoFile_Unavailable()
    {
        var r = ResourceMeasuredChildRunner.Classify(Mk(ProcessOutcome.Timeout, timedOut: true, wrapperExit: true), PathFor("none.txt"));
        Assert.Equal(ResourceMeasurementStatus.Unavailable, r.ResourceStatus);
    }

    [Fact]
    public void ProcessStartError_Unavailable()
    {
        var r = ResourceMeasuredChildRunner.Classify(Mk(ProcessOutcome.ProcessStartError), PathFor("none.txt"));
        Assert.Equal(ResourceMeasurementStatus.Unavailable, r.ResourceStatus);
    }

    [Fact]
    public void StaleFile_RemovedBeforeLaunch_ContentNeverReturned()
    {
        string p = PathFor("stale.txt");
        File.WriteAllText(p, "999 maximum resident set size\n");
        ResourceMeasuredChildRunner.ClearStaleFile(p);
        Assert.False(File.Exists(p));
        // Classification on a completed wrapper must now see Error (missing), never the stale bytes.
        var r = ResourceMeasuredChildRunner.Classify(Mk(ProcessOutcome.CompletedProtocolResult), p);
        Assert.Equal(ResourceMeasurementStatus.Error, r.ResourceStatus);
        Assert.Null(r.ExternalPeakRssBytes);
    }

    [Fact]
    public void MissingParentDirectory_FailsBeforeLaunch()
    {
        string p = PathFor("no-parent/x/stale.txt");
        Assert.Throws<IOException>(() => ResourceMeasuredChildRunner.ClearStaleFile(p));
    }
}

public class CapabilityProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-cap-" + Guid.NewGuid().ToString("N"));
    public CapabilityProbeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string content)
    {
        string p = Path.Combine(_dir, "probe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void ProbeExitNonzero_Fails()
    {
        var r = MacOsTimeCapabilityProbe.Classify(3, Write("0 maximum resident set size"), "", "boom");
        Assert.False(r.Supported);
        Assert.Contains("probe exit code 3", r.Reason);
    }

    [Fact]
    public void ProbeMissingOutput_Fails()
    {
        var r = MacOsTimeCapabilityProbe.Classify(0, Path.Combine(_dir, "absent.txt"), "", "");
        Assert.False(r.Supported);
        Assert.Contains("no -o output", r.Reason);
    }

    [Fact]
    public void ProbeMalformedOutput_Fails()
    {
        var r = MacOsTimeCapabilityProbe.Classify(0, Write("peak memory footprint"), "", "");
        Assert.False(r.Supported);
        Assert.Contains("RSS parse failed", r.Reason);
    }

    [Fact]
    public void ProbeValidOutput_Pass_RetainsRawBytes()
    {
        var r = MacOsTimeCapabilityProbe.Classify(0, Write("424242  maximum resident set size\n"), "", "");
        Assert.True(r.Supported);
        Assert.Equal(424242L, r.RssBytes);
    }
}

using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class ProcessRunnerTests
{
    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "mimir-pr-" + Guid.NewGuid().ToString("N"));
        public string RequestPath { get; }
        public ChildRequestEnvelope Request { get; }
        public string HelperDll => typeof(Mimir.Catalog.BenchmarkCli.TestHelper.Program).Assembly.Location;

        public Fixture()
        {
            Directory.CreateDirectory(Dir);
            Request = new ChildRequestEnvelope
            {
                ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
                CandidateId = CandidateAIdentity.CandidateId,
                CandidateConfigId = CandidateAIdentity.CandidateConfigId,
                WorkloadId = CandidateAIdentity.WorkloadId,
                CorpusId = CandidateAIdentity.CorpusId,
                WorkloadClass = WorkloadClass.Serving,
                Operation = "S1",
                Repetition = 1,
                CandidatePath = "/data path with spaces/candidate.db",
                WorkloadPath = "/data/workload",
                RunId = "run-correlation",
            };
            RequestPath = Path.Combine(Dir, "request.json");
            File.WriteAllText(RequestPath, ProtocolJson.ToJson(Request));
        }

        public ProcessInvocation Invoke(string mode, params string[] extra)
        {
            var args = new List<string> { "exec", HelperDll, mode };
            if (mode is "valid-result" or "invalid-result" or "error-result" or "nonzero-with-valid-json"
                or "stderr-with-valid-result" or "multiple-json" or "mismatch-result")
            {
                args.Add("--request");
                args.Add(RequestPath);
            }
            args.AddRange(extra);
            return new ProcessInvocation { Executable = "dotnet", Arguments = args };
        }

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private static async Task<ChildProcessResult> Run(Fixture f, string mode, TimeSpan? timeout = null, params string[] extra)
        => await ChildProcessRunner.RunAsync(f.Invoke(mode, extra), timeout ?? TimeSpan.FromSeconds(30), f.Request);

    [Theory]
    [InlineData("valid-result", LogicalStatus.Valid)]
    [InlineData("invalid-result", LogicalStatus.Invalid)]
    [InlineData("error-result", LogicalStatus.Error)]
    public async Task Exit0_Correlated_LogicalStatus_Completed(string mode, LogicalStatus status)
    {
        using var f = new Fixture();
        var r = await Run(f, mode);
        Assert.Equal(ProcessOutcome.CompletedProtocolResult, r.Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.NotNull(r.ParsedChildResult);
        Assert.Equal(status, r.ParsedChildResult!.Status);
        Assert.True(r.OutputDrainCompleted);
        Assert.False(r.DescendantTerminationVerified);
    }

    [Fact]
    public async Task MalformedStdout_ProtocolResultError()
    {
        using var f = new Fixture();
        var r = await Run(f, "malformed-stdout");
        Assert.Equal(ProcessOutcome.ProtocolResultError, r.Outcome);
    }

    [Fact]
    public async Task MultipleJsonDocuments_ProtocolResultError()
    {
        using var f = new Fixture();
        var r = await Run(f, "multiple-json");
        Assert.Equal(ProcessOutcome.ProtocolResultError, r.Outcome);
    }

    [Fact]
    public async Task MissingStdout_ProtocolResultError()
    {
        using var f = new Fixture();
        var r = await Run(f, "missing-stdout");
        Assert.Equal(ProcessOutcome.ProtocolResultError, r.Outcome);
    }

    [Fact]
    public async Task NonzeroExit_ValidLookingJson_NotTrusted()
    {
        using var f = new Fixture();
        var r = await Run(f, "nonzero-with-valid-json");
        Assert.Equal(ProcessOutcome.ProcessCrashOrNonzeroExit, r.Outcome);
        Assert.Null(r.ParsedChildResult);
        Assert.Equal(3, r.ExitCode);
    }

    [Fact]
    public async Task StderrDiagnostics_DoNotCorruptStdoutParsing()
    {
        using var f = new Fixture();
        var r = await Run(f, "stderr-with-valid-result");
        Assert.Equal(ProcessOutcome.CompletedProtocolResult, r.Outcome);
        Assert.Contains("helper diagnostic", r.Stderr);
        Assert.NotNull(r.ParsedChildResult);
    }

    [Fact]
    public async Task RequestResultMismatch_ProtocolResultError()
    {
        using var f = new Fixture();
        var r = await Run(f, "mismatch-result");
        Assert.Equal(ProcessOutcome.ProtocolResultError, r.Outcome);
        Assert.Contains("ProtocolVersion mismatch", r.ValidationError);
    }

    [Fact]
    public async Task LargeConcurrentOutput_NoDeadlock()
    {
        using var f = new Fixture();
        var r = await Run(f, "large-output", TimeSpan.FromSeconds(60), "--bytes", "1500000");
        Assert.Equal(ProcessOutcome.ProtocolResultError, r.Outcome); // 'xxxx...' is not a valid JSON document
        Assert.True(r.OutputDrainCompleted);
    }

    [Fact]
    public async Task Timeout_Committed_TimeoutWinsOverPartial_AndKillAttempted()
    {
        using var f = new Fixture();
        var r = await Run(f, "delay", TimeSpan.FromMilliseconds(300), "--ms", "4000");
        Assert.Equal(ProcessOutcome.Timeout, r.Outcome);
        Assert.True(r.TimedOut);
        Assert.True(r.KillAttempted);
        Assert.Null(r.ParsedChildResult);
        Assert.False(r.DescendantTerminationVerified);
    }

    [Fact]
    public async Task NormalExit_WallTimeIndependentOfChildWall()
    {
        using var f = new Fixture();
        var r = await Run(f, "valid-result");
        Assert.True(r.ElapsedParentWallSeconds >= 0);
        Assert.Equal(1.25, r.ParsedChildResult!.WallSeconds);
    }

    [Fact]
    public void Model_ExposesKillStateAndNeverClaimsDescendantTermination()
    {
        var killFailure = new ChildProcessResult
        {
            Outcome = ProcessOutcome.Timeout,
            TimedOut = true,
            KillAttempted = true,
            KillCallSucceeded = false,
            KillError = "process already exited",
            DescendantTerminationVerified = false,
            OutputDrainCompleted = false,
        };
        Assert.True(killFailure.KillAttempted);
        Assert.False(killFailure.KillCallSucceeded);
        Assert.NotNull(killFailure.KillError);
        Assert.False(killFailure.DescendantTerminationVerified);

        var crash = new ChildProcessResult { Outcome = ProcessOutcome.ProcessStartError, DescendantTerminationVerified = false };
        Assert.False(crash.DescendantTerminationVerified);
    }

    [Fact]
    public void ArgumentList_PreservesExactLiteralArguments()
    {
        var executable = "/tmp/bench child.exe";
        var request = "/tmp/a path/request;&|$.json";
        var inv = ProcessInvocation.BenchmarkChild(executable, request);
        Assert.Equal(executable, inv.Executable);
        // ArgumentList + UseShellExecute=false: each element must remain the exact literal string.
        Assert.Equal(3, inv.Arguments.Count);
        Assert.Equal("child", inv.Arguments[0]);
        Assert.Equal("--request", inv.Arguments[1]);
        Assert.Equal(request, inv.Arguments[2]);
    }

    [Fact]
    public async Task MetacharacterRequestPath_ArrivesAsOneExactArgument()
    {
        using var f = new Fixture();
        string weird = Path.Combine(f.Dir, "request file;&|$.json");
        File.WriteAllText(weird, ProtocolJson.ToJson(f.Request));
        var inv = new ProcessInvocation
        {
            Executable = "dotnet",
            Arguments = new[] { "exec", f.HelperDll, "valid-result", "--request", weird },
        };
        var r = await ChildProcessRunner.RunAsync(inv, TimeSpan.FromSeconds(30), f.Request);
        Assert.Equal(ProcessOutcome.CompletedProtocolResult, r.Outcome);
        Assert.Equal(LogicalStatus.Valid, r.ParsedChildResult!.Status);
    }

    [Fact]
    public async Task Timeout_PartialStdout_NeverBecomesProtocolResultError()
    {
        using var f = new Fixture();
        var r = await Run(f, "partial-stdout-then-delay", TimeSpan.FromMilliseconds(300), "--ms", "4000");
        Assert.Equal(ProcessOutcome.Timeout, r.Outcome);
        Assert.True(r.TimedOut);
        Assert.Null(r.ParsedChildResult);
        Assert.True(r.KillAttempted);
        Assert.Contains("protocolVersion", r.Stdout);
        Assert.False(r.DescendantTerminationVerified);
    }

    [Fact]
    public async Task KillFailure_RunnerLevel_TimeoutPreserved_StateExplicit()
    {
        using var f = new Fixture();
        var inv = f.Invoke("delay", "--ms", "1500");
        var r = await ChildProcessRunner.RunAsyncForTest(
            inv, TimeSpan.FromMilliseconds(250), f.Request,
            _ => throw new InvalidOperationException("boom kill"));
        Assert.Equal(ProcessOutcome.Timeout, r.Outcome);
        Assert.True(r.TimedOut);
        Assert.True(r.KillAttempted);
        Assert.False(r.KillCallSucceeded);
        Assert.Contains("boom kill", r.KillError);
        Assert.True(r.WrapperExitObserved);   // child self-exits at 1.5 s within the single cleanup budget
        Assert.True(r.OutputDrainCompleted);
        Assert.False(r.DescendantTerminationVerified);
    }
}

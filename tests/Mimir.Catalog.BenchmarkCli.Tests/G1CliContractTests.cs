using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class G1CliContractTests : IDisposable
{
    private sealed class GraphCandidate : IStorageCandidate
    {
        public bool ThrowOnOpen { get; init; }
        public bool ThrowOnSubclassOf { get; init; }
        public bool ReturnParent { get; init; }
        public void Open() { if (ThrowOnOpen) throw new InvalidOperationException("open boom"); }
        public void Dispose() { }
        public ConceptHit GetConcept(long qid) => new(true, true, false);
        public IReadOnlyList<LexicalHit> LookupLexical(string lang, string value) => Array.Empty<LexicalHit>();
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long qid) => Array.Empty<LexicalRow>();
        public IReadOnlyList<long> GetInstanceOf(long qid) => Array.Empty<long>();
        public IReadOnlyList<long> GetSubclassOf(long qid)
        {
            if (ThrowOnSubclassOf) throw new InvalidOperationException("traverse boom");
            return ReturnParent ? new[] { qid + 1_000_000 } : Array.Empty<long>();
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-g1-cli-" + Guid.NewGuid().ToString("N"));

    public G1CliContractTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string EmptyDigest() => WorkloadOracle.G1Digest(Array.Empty<long>(), 1);

    private static ChildRequestEnvelope Request(WorkloadClass workloadClass = WorkloadClass.G1, string operation = "G1") => new()
    {
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        WorkloadClass = workloadClass,
        Operation = operation,
        Repetition = 1,
        CandidatePath = "/fixture/candidate.db",
        WorkloadPath = "/fixture/workload",
        RunId = "run-cli-g1",
    };

    private string RequestPath() => Path.Combine(_dir, "example.request.json");

    private static GraphWorkload G1Workload()
    {
        var probe = new GraphProbe("G1", 0, "Degree1", true, 1000);
        return new GraphWorkload
        {
            Probes = new[] { probe },
            Expected = new Dictionary<(string, long), GraphExpected> { [("G1", 0L)] = new("G1", 0, true, 0, 1, EmptyDigest()) },
        };
    }

    private (int Exit, string Stdout) Run(Func<string, GraphWorkload> loader, IStorageCandidate candidate)
    {
        var request = Request();
        File.WriteAllText(RequestPath(), ProtocolJson.ToJson(request));
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        try
        {
            Console.SetOut(outW);
            Console.SetError(errW);
            int exit = Program.RunG1ChildCore(request, RequestPath(), loader, () => candidate);
            return (exit, outW.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void ValidRun_Exit0_EnvelopeAndArtifact()
    {
        var (exit, stdout) = Run(_ => G1Workload(), new GraphCandidate());
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Valid, env.Status);
        Assert.Equal("VALID", env.CorrectnessStatus);
        Assert.Equal(WorkloadClass.G1, env.WorkloadClass);
        Assert.Equal("G1", env.Operation);
        Assert.Null(env.ResultCardinality);
        Assert.Null(env.ResultDigest);
        string artifact = Program.G1ArtifactPath(RequestPath());
        Assert.True(File.Exists(artifact));
        Assert.NotEmpty(File.ReadAllText(artifact).Trim());
    }

    [Fact]
    public void InvalidRun_ZeroSamples_Exit0_Envelope()
    {
        var (exit, stdout) = Run(_ => G1Workload(), new GraphCandidate { ReturnParent = true });
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Invalid, env.Status);
        Assert.Equal("INVALID", env.CorrectnessStatus);
        Assert.Null(env.ErrorCategory);
        Assert.Equal(0, new FileInfo(Program.G1ArtifactPath(RequestPath())).Length);
    }

    [Fact]
    public void ErrorRun_WarmupCategory_Exit0_EnvelopeAndEmptyArtifact()
    {
        var (exit, stdout) = Run(_ => G1Workload(), new GraphCandidate { ThrowOnSubclassOf = true });
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Error, env.Status);
        Assert.Equal("ERROR", env.CorrectnessStatus);
        Assert.Equal("warmup", env.ErrorCategory);
        Assert.NotNull(env.ErrorMessage);
        Assert.Equal(0, new FileInfo(Program.G1ArtifactPath(RequestPath())).Length);
    }

    [Fact]
    public void RuntimeOpenFailure_ErrorCategoryRuntime_Exit0_EmptyArtifact()
    {
        var (exit, stdout) = Run(_ => G1Workload(), new GraphCandidate { ThrowOnOpen = true });
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Error, env.Status);
        Assert.Equal("runtime", env.ErrorCategory);
        Assert.NotNull(env.ErrorMessage);
        Assert.Equal(0, new FileInfo(Program.G1ArtifactPath(RequestPath())).Length);
    }

    [Fact]
    public void WorkloadLoadFailure_Nonzero_NoEnvelope_NoArtifact()
    {
        var (exit, stdout) = Run(_ => throw new InvalidDataException("missing package"), new GraphCandidate());
        Assert.NotEqual(0, exit);
        Assert.Empty(stdout);
        Assert.False(File.Exists(Program.G1ArtifactPath(RequestPath())));
    }

    [Fact]
    public void ArtifactCollision_Nonzero_NoEnvelope_PreservesExisting()
    {
        string artifact = Program.G1ArtifactPath(RequestPath());
        File.WriteAllText(artifact, "keep-me");
        var (exit, stdout) = Run(_ => G1Workload(), new GraphCandidate());
        Assert.NotEqual(0, exit);
        Assert.Empty(stdout);
        Assert.Equal("keep-me", File.ReadAllText(artifact));
    }

    [Fact]
    public void ArtifactPathConvention()
    {
        Assert.Equal(Path.Combine(_dir, "example.request.g1-samples.jsonl"),
            Program.G1ArtifactPath(RequestPath()));
    }

    private int MainChild(WorkloadClass workloadClass, string operation, string workloadPath = "/fixture/workload")
    {
        var request = Request(workloadClass, operation);
        File.WriteAllText(RequestPath(), ProtocolJson.ToJson(request));
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        try
        {
            Console.SetOut(outW);
            Console.SetError(errW);
            return Program.Main(new[] { "child", "--request", RequestPath() });
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void G1Dispatch_ReachesWorkloadLoading_NotExecutionNotImplemented()
    {
        // The G1 branch is entered (workload load fails against a bogus path and
        // returns FatalProtocolError=1) rather than falling to ExecutionNotImplemented=3.
        int exit = MainChild(WorkloadClass.G1, "G1", workloadPath: Path.Combine(_dir, "missing-workload"));
        Assert.Equal(ProtocolExitCodes.FatalProtocolError, exit);
        Assert.False(File.Exists(Program.G1ArtifactPath(RequestPath())));
    }

    [Fact]
    public void G2Dispatch_StillExecutionNotImplemented()
    {
        Assert.Equal(ProtocolExitCodes.ExecutionNotImplemented, MainChild(WorkloadClass.G2, "G2"));
    }
}

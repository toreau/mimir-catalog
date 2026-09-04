using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class G2CliContractTests : IDisposable
{
    private sealed class G2Candidate : IStorageCandidate
    {
        public bool ThrowOnOpen { get; init; }
        public bool ThrowOnInstanceOf { get; init; }
        public bool ReturnTarget { get; init; }
        public void Open() { if (ThrowOnOpen) throw new InvalidOperationException("open boom"); }
        public void Dispose() { }
        public ConceptHit GetConcept(long qid) => new(true, true, false);
        public IReadOnlyList<LexicalHit> LookupLexical(string lang, string value) => Array.Empty<LexicalHit>();
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long qid) => Array.Empty<LexicalRow>();
        public IReadOnlyList<long> GetInstanceOf(long qid)
        {
            if (ThrowOnInstanceOf) throw new InvalidOperationException("instance boom");
            return ReturnTarget ? new[] { qid + 1_000_000 } : Array.Empty<long>();
        }
        public IReadOnlyList<long> GetSubclassOf(long qid) => Array.Empty<long>();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-g2-cli-" + Guid.NewGuid().ToString("N"));

    public G2CliContractTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ChildRequestEnvelope Request(WorkloadClass workloadClass = WorkloadClass.G2, string operation = "G2") => new()
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
        RunId = "run-cli-g2",
    };

    private string RequestPath() => Path.Combine(_dir, "example.request.json");

    private static G2Workload Workload(int count = 3)
    {
        var concepts = new List<G2Concept>();
        var perInput = new List<G2PerInputExpected>();
        for (int i = 0; i < count; i++)
        {
            concepts.Add(new G2Concept(1000 + i, i % 2 == 0 ? "P31Degree1" : "P31Degree2Plus"));
            perInput.Add(new G2PerInputExpected(i, 1000 + i, i % 2 == 0 ? "P31Degree1" : "P31Degree2Plus", 0,
                WorkloadOracle.StructuralSetDigest(Array.Empty<long>())));
        }
        var rows = concepts.Select(c => (c.Qid, Array.Empty<long>())).ToList();
        return new G2Workload { Concepts = concepts, PerInput = perInput, Batch = new G2BatchExpected(count, WorkloadOracle.G2BatchDigest(rows)) };
    }

    private (int Exit, string Stdout) Run(Func<string, G2Workload> loader, IStorageCandidate candidate)
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
            int exit = Program.RunG2ChildCore(request, RequestPath(), loader, () => candidate);
            return (exit, outW.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void ValidRun_Exit0_EnvelopeWithBatchFactsAndArtifact()
    {
        var (exit, stdout) = Run(_ => Workload(), new G2Candidate());
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Valid, env.Status);
        Assert.Equal("VALID", env.CorrectnessStatus);
        Assert.Equal(WorkloadClass.G2, env.WorkloadClass);
        Assert.Equal("G2", env.Operation);
        Assert.NotNull(env.WallSeconds);
        Assert.Equal(3, env.ResultCardinality);
        Assert.NotNull(env.ResultDigest);
        string artifact = Program.G2ArtifactPath(RequestPath());
        Assert.True(File.Exists(artifact));
        string text = File.ReadAllText(artifact);
        Assert.Equal(4, text.TrimEnd('\n').Split('\n').Length); // 3 per-input + 1 batch
    }

    [Fact]
    public void WarmupInvalidRun_NoTimedBatch_EnvelopeNoFacts()
    {
        var (exit, stdout) = Run(_ => Workload(), new G2Candidate { ReturnTarget = true });
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Invalid, env.Status);
        Assert.Equal("INVALID", env.CorrectnessStatus);
        Assert.Null(env.WallSeconds);
        Assert.Null(env.ResultCardinality);
        Assert.Null(env.ResultDigest);
        Assert.Equal(0, new FileInfo(Program.G2ArtifactPath(RequestPath())).Length);
    }

    [Fact]
    public void ErrorRun_WarmupCategory_Exit0_EmptyArtifact()
    {
        var (exit, stdout) = Run(_ => Workload(), new G2Candidate { ThrowOnInstanceOf = true });
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Error, env.Status);
        Assert.Equal("warmup", env.ErrorCategory);
        Assert.NotNull(env.ErrorMessage);
        Assert.Equal(0, new FileInfo(Program.G2ArtifactPath(RequestPath())).Length);
    }

    [Fact]
    public void RuntimeOpenFailure_Exit0_RuntimeCategory_EmptyArtifact()
    {
        var (exit, stdout) = Run(_ => Workload(), new G2Candidate { ThrowOnOpen = true });
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Error, env.Status);
        Assert.Equal("runtime", env.ErrorCategory);
        Assert.Null(env.WallSeconds);
        Assert.Equal(0, new FileInfo(Program.G2ArtifactPath(RequestPath())).Length);
    }

    [Fact]
    public void WorkloadLoadFailure_Nonzero_NoEnvelope_NoArtifact()
    {
        var (exit, stdout) = Run(_ => throw new InvalidDataException("missing package"), new G2Candidate());
        Assert.NotEqual(0, exit);
        Assert.Empty(stdout);
        Assert.False(File.Exists(Program.G2ArtifactPath(RequestPath())));
    }

    [Fact]
    public void ArtifactCollision_Nonzero_NoEnvelope_PreservesExisting()
    {
        string artifact = Program.G2ArtifactPath(RequestPath());
        File.WriteAllText(artifact, "keep-me");
        var (exit, stdout) = Run(_ => Workload(), new G2Candidate());
        Assert.NotEqual(0, exit);
        Assert.Empty(stdout);
        Assert.Equal("keep-me", File.ReadAllText(artifact));
    }

    [Fact]
    public void ArtifactPathConvention()
    {
        Assert.Equal(Path.Combine(_dir, "example.request.g2-results.jsonl"),
            Program.G2ArtifactPath(RequestPath()));
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
    public void G2Dispatch_ReachesWorkloadLoading_NotExecutionNotImplemented()
    {
        int exit = MainChild(WorkloadClass.G2, "G2", workloadPath: Path.Combine(_dir, "missing-workload"));
        Assert.Equal(ProtocolExitCodes.FatalProtocolError, exit);
        Assert.False(File.Exists(Program.G2ArtifactPath(RequestPath())));
    }

    [Theory]
    [InlineData(WorkloadClass.Analytical, "A1")]
    [InlineData(WorkloadClass.Open, "open_ready")]
    [InlineData(WorkloadClass.Build, "build")]
    public void LaterWorkloadClasses_RemainExecutionNotImplemented(WorkloadClass workloadClass, string operation)
    {
        Assert.Equal(ProtocolExitCodes.ExecutionNotImplemented, MainChild(workloadClass, operation));
    }
}

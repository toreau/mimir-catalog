using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class ServingCliContractTests : IDisposable
{
    private sealed class FixtureCandidate : IStorageCandidate
    {
        public bool Present { get; init; } = true;
        public bool ThrowOnConcept { get; init; }
        public void Open() { }
        public void Dispose() { }
        public ConceptHit GetConcept(long qid)
        {
            if (ThrowOnConcept) throw new InvalidOperationException("concept boom");
            return new ConceptHit(Present, true, false);
        }
        public IReadOnlyList<LexicalHit> LookupLexical(string lang, string value) => Array.Empty<LexicalHit>();
        public IReadOnlyList<LexicalRow> GetLexicalByQid(long qid) => Array.Empty<LexicalRow>();
        public IReadOnlyList<long> GetInstanceOf(long subjectQid) => Array.Empty<long>();
        public IReadOnlyList<long> GetSubclassOf(long subjectQid) => Array.Empty<long>();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-svc-cli-" + Guid.NewGuid().ToString("N"));

    public ServingCliContractTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ChildRequestEnvelope Request() => new()
    {
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        WorkloadClass = WorkloadClass.Serving,
        Operation = "S1",
        Repetition = 1,
        CandidatePath = "/fixture/candidate.db",
        WorkloadPath = "/fixture/workload",
        RunId = "run-cli",
    };

    private string RequestPath()
        => Path.Combine(_dir, "example.request.json");

    private static ServingWorkload S1Workload()
    {
        var probe = new ServingProbe("S1", 1, "Hit", true, 100, null, null);
        var expected = new Dictionary<(string, long), ServingExpected>
        {
            [("S1", 1L)] = new("S1", 1, true, 1,
                WorkloadOracle.ConceptResultDigest(100, true, true, false)),
        };
        return new ServingWorkload { Probes = new[] { probe }, Expected = expected };
    }

    private (int Exit, string Stdout) Run(Func<string, ServingWorkload> loader, IStorageCandidate candidate)
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
            int exit = Program.RunServingChildCore(request, RequestPath(), loader, () => candidate);
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
        var (exit, stdout) = Run(_ => S1Workload(), new FixtureCandidate());
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Valid, env.Status);
        Assert.Equal("VALID", env.CorrectnessStatus);
        Assert.Null(env.ResultCardinality);
        Assert.Null(env.ResultDigest);
        Assert.Null(env.ErrorCategory);
        Assert.True(File.Exists(Program.ServingArtifactPath(RequestPath())));
    }

    [Fact]
    public void InvalidRun_Exit0_Envelope()
    {
        var (exit, stdout) = Run(_ => S1Workload(), new FixtureCandidate { Present = false });
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Invalid, env.Status);
        Assert.Equal("INVALID", env.CorrectnessStatus);
        Assert.Null(env.ErrorCategory);
        Assert.True(File.Exists(Program.ServingArtifactPath(RequestPath())));
    }

    [Fact]
    public void ErrorRun_Exit0_DiagnosticsAndArtifact()
    {
        var (exit, stdout) = Run(_ => S1Workload(), new FixtureCandidate { ThrowOnConcept = true });
        Assert.Equal(0, exit);
        var env = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(LogicalStatus.Error, env.Status);
        Assert.Equal("ERROR", env.CorrectnessStatus);
        Assert.Equal("warmup", env.ErrorCategory);
        Assert.NotNull(env.ErrorMessage);
        Assert.True(File.Exists(Program.ServingArtifactPath(RequestPath())));
    }

    [Fact]
    public void WorkloadLoadFailure_Nonzero_NoEnvelope_NoArtifact()
    {
        var (exit, stdout) = Run(_ => throw new InvalidDataException("missing package"), new FixtureCandidate());
        Assert.NotEqual(0, exit);
        Assert.Empty(stdout);
        Assert.False(File.Exists(Program.ServingArtifactPath(RequestPath())));
    }

    [Fact]
    public void ArtifactCollision_Nonzero_NoEnvelope_PreservesExisting()
    {
        string artifact = Program.ServingArtifactPath(RequestPath());
        File.WriteAllText(artifact, "keep-me");
        var (exit, stdout) = Run(_ => S1Workload(), new FixtureCandidate());
        Assert.NotEqual(0, exit);
        Assert.Empty(stdout);
        Assert.Equal("keep-me", File.ReadAllText(artifact));
    }

    [Fact]
    public void ArtifactPathConvention()
    {
        Assert.Equal(Path.Combine(_dir, "example.request.serving-samples.jsonl"),
            Program.ServingArtifactPath(RequestPath()));
    }
}

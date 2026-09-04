using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class G2ChildOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-g2o-" + Guid.NewGuid().ToString("N"));
    private static string EmptyDigest => WorkloadOracle.StructuralSetDigest(Array.Empty<long>());
    private static readonly string BatchDigestFor = BatchDigest();

    private static string BatchDigest(int n = 3)
        => WorkloadOracle.G2BatchDigest(Enumerable.Range(0, n).Select(i => (1000L + i, Array.Empty<long>())).ToList());

    public G2ChildOrchestratorTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static RunIdentity Identity(string? candidateId = null) => new()
    {
        EvidenceSchemaVersion = EvidenceSchema.Version,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = candidateId ?? CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = "run-1",
    };

    private static G2Workload Workload(int count = 3)
    {
        var concepts = new List<G2Concept>();
        var perInput = new List<G2PerInputExpected>();
        for (int i = 0; i < count; i++)
        {
            concepts.Add(new G2Concept(1000 + i, i % 2 == 0 ? "P31Degree1" : "P31Degree2Plus"));
            perInput.Add(new G2PerInputExpected(i, 1000 + i, i % 2 == 0 ? "P31Degree1" : "P31Degree2Plus", 0, EmptyDigest));
        }
        var rows = concepts.Select(c => (c.Qid, Array.Empty<long>())).ToList();
        return new G2Workload { Concepts = concepts, PerInput = perInput, Batch = new G2BatchExpected(count, WorkloadOracle.G2BatchDigest(rows)) };
    }

    private static G2TimedPerInputResult RawValid(int item) => new(item, 1000 + item,
        item % 2 == 0 ? "P31Degree1" : "P31Degree2Plus", ServingStatuses.Valid, 0, EmptyDigest);

    private static G2TimedPerInputResult RawInvalid(int item) => new(item, 1000 + item,
        item % 2 == 0 ? "P31Degree1" : "P31Degree2Plus", ServingStatuses.Invalid, 7, "zzz");

    private static G2TimedPerInputResult RawError(int item, string message) => new(item, 1000 + item,
        item % 2 == 0 ? "P31Degree1" : "P31Degree2Plus", ServingStatuses.Error, Error: message);

    private static G2TimedBatchResult RawBatch(double wall = 10.0, string status = "VALID", long? card = 3, string? digest = null, string? error = null)
        => new(wall, status, card,
            digest ?? (status == ServingStatuses.Valid ? BatchDigest() : status == ServingStatuses.Error ? null : "wrong"), error);

    private ChildResultEnvelope Envelope(string correctness, LogicalStatus? statusOverride = null, string? category = null,
        string? message = null, double? wall = null, long? card = null, string? digest = null, int repetition = 1)
        => new()
        {
            ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
            CandidateId = CandidateAIdentity.CandidateId,
            CandidateConfigId = CandidateAIdentity.CandidateConfigId,
            WorkloadId = CandidateAIdentity.WorkloadId,
            CorpusId = CandidateAIdentity.CorpusId,
            WorkloadClass = WorkloadClass.G2,
            Operation = "G2",
            Repetition = repetition,
            Status = statusOverride ?? (correctness switch { "VALID" => LogicalStatus.Valid, "INVALID" => LogicalStatus.Invalid, _ => LogicalStatus.Error }),
            CorrectnessStatus = correctness,
            WallSeconds = wall,
            ResultCardinality = card,
            ResultDigest = digest,
            ErrorCategory = category,
            ErrorMessage = message,
        };

    private static ChildProcessResult Process(ProcessOutcome outcome, ChildResultEnvelope? env, int? exitCode = 0)
        => new()
        {
            Outcome = outcome,
            TimedOut = outcome == ProcessOutcome.Timeout,
            ExitCode = exitCode,
            Stdout = env is null ? "" : ProtocolJson.ToJson(env),
            Stderr = "",
            WrapperExitObserved = outcome != ProcessOutcome.Timeout,
            OutputDrainCompleted = true,
            ParsedChildResult = env,
            DescendantTerminationVerified = false,
            ValidationError = null,
        };

    private static ChildProcessResult TimeoutProcess(bool wrapperObserved)
        => new()
        {
            Outcome = ProcessOutcome.Timeout,
            TimedOut = true,
            ExitCode = null,
            WrapperExitObserved = wrapperObserved,
            OutputDrainCompleted = false,
            DescendantTerminationVerified = false,
        };

    private EvidenceStagingSession NewSession(string? candidateId = null)
    {
        string runs = Path.Combine(_root, "runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runs);
        return EvidenceStagingSession.Create(runs, Identity(candidateId));
    }

    private string SampleRel => "graph/g2/rep-1/request.g2-results.jsonl";
    private string ResourceRel => "graph/g2/rep-1/resource-time.txt";

    private string Physical(EvidenceStagingSession session, string rel)
        => Path.Combine(session.StagingPath, rel.Replace('/', Path.DirectorySeparatorChar));

    private async Task<G2ChildEvidenceResult> Run(
        EvidenceStagingSession session,
        G2Workload workload,
        ChildProcessResult process,
        ResourceMeasurementStatus resourceStatus = ResourceMeasurementStatus.Valid,
        bool createResourceFile = true,
        Action<string>? sampleProducer = null)
    {
        string resourcePhysical = Physical(session, ResourceRel);
        long? rss = resourceStatus == ResourceMeasurementStatus.Valid ? 1234L : null;
        return await G2ChildOrchestrator.RunAsync(
            session, 1, "/fixture/candidate.db", "/fixture/workload", workload,
            reqPath => ProcessInvocation.BenchmarkChild("/fixture/child.exe", reqPath),
            TimeSpan.FromSeconds(3600),
            (_, rp, _, _) =>
            {
                if (resourceStatus == ResourceMeasurementStatus.Valid && createResourceFile && !File.Exists(rp))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(rp)!);
                    File.WriteAllText(rp, "1  maximum resident set size\n");
                }
                return Task.FromResult(new ResourceMeasuredProcessResult
                {
                    ProcessResult = process,
                    ResourceStatus = resourceStatus,
                    ExternalPeakRssBytes = rss,
                    ResourceOutputPath = resourcePhysical,
                });
            },
            sampleProducer).ConfigureAwait(false);
    }

    private static void WriteArtifact(string path, IReadOnlyList<G2TimedPerInputResult> perInput, G2TimedBatchResult? batch)
        => G2ResultArtifact.WriteCreateNew(path, perInput, batch);

    private IReadOnlyList<string> Artifacts(EvidenceStagingSession session)
        => session.RegisteredArtifacts.Select(a => a.RelativePath).ToList();

    private static IReadOnlyList<G2TimedPerInputResult> ValidInputs(int n)
        => Enumerable.Range(0, n).Select(RawValid).ToList();

    [Fact]
    public async Task ValidFull_EvidenceAndFacts()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0, card: 3, digest: BatchDigest())),
            sampleProducer: p => WriteArtifact(p, ValidInputs(3), RawBatch()));
        Assert.True(r.EvidenceValid);
        Assert.True(r.RegisteredStableArtifacts);
        Assert.True(r.TimedBatchComplete);
        Assert.Equal(3, r.PerInput.Count);
        Assert.All(r.PerInput, pi => Assert.Equal(ServingStatuses.Valid, pi.ChildCorrectness));
        Assert.NotNull(r.Batch);
        Assert.Equal(TimedResultStatus.Valid, r.Batch!.Status);
        Assert.Equal(10.0, r.Batch.WallSeconds);
        Assert.Contains(SampleRel, Artifacts(s));
    }

    [Fact]
    public async Task Wall120_ClassifiedTimeout()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 120.0, card: 3, digest: BatchDigest())),
            sampleProducer: p => WriteArtifact(p, ValidInputs(3), RawBatch(wall: 120.0)));
        Assert.True(r.EvidenceValid);
        Assert.Equal(TimedResultStatus.Timeout, r.Batch!.Status);
    }

    [Fact]
    public async Task TimedInvalidComplete_RetainsBatchFacts()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("INVALID", wall: 10.0, card: 3, digest: "wrong")),
            sampleProducer: p => WriteArtifact(p, new[] { RawValid(0), RawInvalid(1), RawValid(2) }, RawBatch(status: "INVALID", digest: "wrong")));
        Assert.True(r.EvidenceValid);
        Assert.True(r.TimedBatchComplete);
        Assert.Equal(TimedResultStatus.Invalid, r.Batch!.Status);
        Assert.Equal("INVALID", r.PerInput[1].ChildCorrectness);
        Assert.NotNull(r.Batch.ActualDigest);
    }

    [Fact]
    public async Task TimedBatchError_Complete_EnvelopeMessageEqualsRawBatch()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("ERROR", category: "timed-batch", message: "boom", wall: 10.0)),
            sampleProducer: p => WriteArtifact(p, new[] { RawValid(0), RawError(1, "boom"), RawValid(2) }, RawBatch(status: "ERROR", card: null, digest: null, error: "boom")));
        Assert.True(r.EvidenceValid);
        Assert.True(r.TimedBatchComplete); // structural N+1 even though Batch correctness is ERROR
        Assert.Equal(TimedResultStatus.Error, r.Batch!.Status);
        Assert.Equal(ServingStatuses.Error, r.PerInput[1].ChildCorrectness);
        Assert.Equal(ServingStatuses.Valid, r.PerInput[2].ChildCorrectness);
        Assert.Equal("boom", r.Envelope!.ErrorMessage);
    }

    [Theory]
    [InlineData("INVALID", null, null)]
    [InlineData("ERROR", "warmup", "warm")]
    [InlineData("ERROR", "runtime", "run")]
    public async Task ZeroRawForms_Legitimate(string correctness, string? category, string? message)
    {
        using var s = NewSession();
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope(correctness, category: category, message: message)),
            sampleProducer: p => WriteArtifact(p, Array.Empty<G2TimedPerInputResult>(), null));
        Assert.True(r.EvidenceValid);
        Assert.Empty(r.PerInput);
        Assert.Null(r.Batch);
        Assert.False(r.TimedBatchComplete);
    }

    [Fact]
    public async Task FalseValidPerInput_IntegrityFailure()
    {
        using var s = NewSession();
        var bad = RawValid(0) with { ActualDigest = "zzz" };
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0, card: 3, digest: BatchDigest())),
            sampleProducer: p => WriteArtifact(p, new[] { bad, RawValid(1), RawValid(2) }, RawBatch()));
        Assert.False(r.EvidenceValid);
        Assert.DoesNotContain(r.PerInput, pi => pi.Item == 0);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("digest mismatch"));
    }

    [Fact]
    public async Task ExplicitNullInArtifact_Malformed()
    {
        using var s = NewSession();
        string line = "{\"kind\":\"batch\",\"operation\":\"G2\",\"sequence\":500,\"wall_seconds\":10," +
                      "\"correctness_status\":\"VALID\",\"actual_cardinality\":3,\"actual_digest\":null}\n";
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0, card: 3, digest: BatchDigest())),
            sampleProducer: p => File.WriteAllBytes(p, System.Text.Encoding.UTF8.GetBytes(line)));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("malformed G2 result artifact"));
    }

    [Fact]
    public async Task EnvelopeResultMismatch_Fails()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0, card: 999, digest: BatchDigest())),
            sampleProducer: p => WriteArtifact(p, ValidInputs(3), RawBatch()));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("must exactly equal raw Batch"));
    }

    [Fact]
    public async Task EnvelopeMappingMismatch_Fails()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("INVALID", statusOverride: LogicalStatus.Valid, wall: 10.0, card: 3, digest: "wrong")),
            sampleProducer: p => WriteArtifact(p, new[] { RawValid(0), RawInvalid(1), RawValid(2) }, RawBatch(status: "INVALID", digest: "wrong")));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("inconsistent with CorrectnessStatus"));
    }

    [Fact]
    public async Task MissingSampleAfterExit0_Fails()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0, card: 3, digest: BatchDigest())));
        Assert.False(r.EvidenceValid);
        Assert.False(r.RegisteredStableArtifacts);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("missing G2 result artifact"));
    }

    [Fact]
    public async Task TimeoutForensics_ObservedRegisters_NotObservedSkips()
    {
        using var s1 = NewSession();
        var r1 = await Run(s1, Workload(), TimeoutProcess(wrapperObserved: true),
            sampleProducer: p => WriteArtifact(p, ValidInputs(3), RawBatch()));
        Assert.False(r1.EvidenceValid);
        Assert.Empty(r1.PerInput);
        Assert.Null(r1.Batch);
        Assert.False(r1.TimedBatchComplete);
        Assert.Contains(SampleRel, Artifacts(s1));

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(), TimeoutProcess(wrapperObserved: false),
            sampleProducer: p => WriteArtifact(p, ValidInputs(3), RawBatch()));
        Assert.DoesNotContain(SampleRel, Artifacts(s2));
    }

    [Fact]
    public async Task ResourceOrthogonal_AndValidMissingFile()
    {
        using var s1 = NewSession();
        var r1 = await Run(s1, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0, card: 3, digest: BatchDigest())),
            ResourceMeasurementStatus.Error, createResourceFile: false,
            sampleProducer: p => WriteArtifact(p, ValidInputs(3), RawBatch()));
        Assert.True(r1.EvidenceValid);
        Assert.Equal(ResourceMeasurementStatus.Error, r1.ResourceStatus);

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0, card: 3, digest: BatchDigest())),
            createResourceFile: false,
            sampleProducer: p => WriteArtifact(p, ValidInputs(3), RawBatch()));
        Assert.False(r2.EvidenceValid);
        Assert.Contains(r2.EvidenceProblems, x => x.Contains("raw resource output file missing"));
    }

    [Fact]
    public async Task IdentityMismatch_FailsBeforeChildExecution()
    {
        using var s = NewSession(candidateId: "wrong");
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0, card: 3, digest: BatchDigest())),
            sampleProducer: p => WriteArtifact(p, ValidInputs(3), RawBatch()));
        Assert.Equal(ProcessOutcome.ParentError, r.ProcessOutcome);
        Assert.False(r.EvidenceValid);
        Assert.Empty(Artifacts(s));
    }

    [Fact]
    public async Task DeterministicEvidence_NoPublication()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0, card: 3, digest: BatchDigest())),
            sampleProducer: p => WriteArtifact(p, ValidInputs(3), RawBatch()));
        Assert.True(r.EvidenceValid);
        string execution = File.ReadAllText(Physical(s, "graph/g2/rep-1/execution.json"));
        Assert.Contains("\"timed_batch_complete\":true", execution);
        Assert.Contains("\"per_input_valid_count\":3", execution);
        Assert.Contains("\"batch\"", execution);
        string processJson = File.ReadAllText(Physical(s, "graph/g2/rep-1/process.json"));
        Assert.Contains("killAttempted", processJson);
    }
}

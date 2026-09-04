using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class G1ChildOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-g1o-" + Guid.NewGuid().ToString("N"));
    private static string Digest => WorkloadOracle.G1Digest(Array.Empty<long>(), 1);

    public G1ChildOrchestratorTests() => Directory.CreateDirectory(_root);
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

    private static GraphWorkload Workload(int count)
    {
        var probes = new List<GraphProbe>();
        var expected = new Dictionary<(string, long), GraphExpected>();
        for (int seq = 0; seq < count; seq++)
        {
            probes.Add(new GraphProbe("G1", seq, seq % 2 == 0 ? "Degree1" : "Degree2Plus", true, 1000 + seq));
            expected[("G1", seq)] = new GraphExpected("G1", seq, true, 0, 1, Digest);
        }
        return new GraphWorkload { Probes = probes, Expected = expected };
    }

    private static G1TimedSample Sample(long seq, string status = "VALID", double wall = 0.5, long card = 0, long visited = 1, string? digest = null, string? error = null)
        => new("G1", seq, seq % 2 == 0 ? "Degree1" : "Degree2Plus", wall, status, card, visited, digest ?? Digest, error);

    private static G1TimedSample ErrorSample(long seq, string message)
        => new("G1", seq, seq % 2 == 0 ? "Degree1" : "Degree2Plus", 0.5, "ERROR", Error: message);

    private ChildResultEnvelope Envelope(string correctness, string? category = null, string? message = null, double? wall = null, int repetition = 1)
        => new()
        {
            ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
            CandidateId = CandidateAIdentity.CandidateId,
            CandidateConfigId = CandidateAIdentity.CandidateConfigId,
            WorkloadId = CandidateAIdentity.WorkloadId,
            CorpusId = CandidateAIdentity.CorpusId,
            WorkloadClass = WorkloadClass.G1,
            Operation = "G1",
            Repetition = repetition,
            Status = correctness switch { "VALID" => LogicalStatus.Valid, "INVALID" => LogicalStatus.Invalid, _ => LogicalStatus.Error },
            CorrectnessStatus = correctness,
            WallSeconds = wall,
            ResultCardinality = null,
            ResultDigest = null,
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

    private string SampleRel => "graph/g1/rep-1/request.g1-samples.jsonl";
    private string ResourceRel => "graph/g1/rep-1/resource-time.txt";

    private string Physical(EvidenceStagingSession session, string rel)
        => Path.Combine(session.StagingPath, rel.Replace('/', Path.DirectorySeparatorChar));

    private async Task<G1ChildEvidenceResult> Run(
        EvidenceStagingSession session,
        GraphWorkload workload,
        ChildProcessResult process,
        ResourceMeasurementStatus resourceStatus = ResourceMeasurementStatus.Valid,
        bool createResourceFile = true,
        Action<string>? sampleProducer = null)
    {
        string resourcePhysical = Physical(session, ResourceRel);
        long? rss = resourceStatus == ResourceMeasurementStatus.Valid ? 1234L : null;
        return await G1ChildOrchestrator.RunAsync(
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

    private IReadOnlyList<string> Artifacts(EvidenceStagingSession session)
        => session.RegisteredArtifacts.Select(a => a.RelativePath).ToList();

    [Fact]
    public async Task ValidFull_EvidenceAndSamples()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(3), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0), Sample(1), Sample(2) }));
        Assert.True(r.EvidenceValid);
        Assert.True(r.RegisteredStableArtifacts);
        Assert.True(r.MeasuredSequenceComplete);
        Assert.Equal(3, r.ParentSamples.Count);
        Assert.All(r.ParentSamples, ps => Assert.Equal(TimedResultStatus.Valid, ps.Status));
        Assert.Contains(SampleRel, Artifacts(s));
        Assert.Contains(ResourceRel, Artifacts(s));
    }

    [Fact]
    public async Task WallAt30_ClassifiedTimeout_EvidenceValid()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 30.0)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0, wall: 30.0) }));
        Assert.True(r.EvidenceValid);
        Assert.Equal(TimedResultStatus.Timeout, r.ParentSamples[0].Status);
    }

    [Fact]
    public async Task TimedStartError_CompleteFinalProbe_Legitimate()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(3), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("ERROR", "timed-start", "boom", wall: 10.0)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0), Sample(1), ErrorSample(2, "boom") }));
        Assert.True(r.EvidenceValid);
        Assert.True(r.MeasuredSequenceComplete); // error on the final expected probe
        Assert.Equal(3, r.ParentSamples.Count);
        Assert.Equal(TimedResultStatus.Error, r.ParentSamples[2].Status);
        Assert.Equal(TimedResultStatus.Valid, r.ParentSamples[0].Status);
    }

    [Fact]
    public async Task TimedStartError_ShortPrefix_Legitimate_Incomplete()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(5), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("ERROR", "timed-start", "boom", wall: 10.0)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0), ErrorSample(1, "boom") }));
        Assert.True(r.EvidenceValid);
        Assert.False(r.MeasuredSequenceComplete);
        Assert.Equal(2, r.ParentSamples.Count);
    }

    [Theory]
    [InlineData("warmup", 0.0)]
    [InlineData("runtime", 0.0)]
    public async Task ZeroRawErrorForms_Legitimate(string category, double _)
    {
        using var s = NewSession();
        var r = await Run(s, Workload(3), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("ERROR", category, "some failure")),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, Array.Empty<G1TimedSample>()));
        Assert.True(r.EvidenceValid);
        Assert.Empty(r.ParentSamples);
        Assert.False(r.MeasuredSequenceComplete);
    }

    [Fact]
    public async Task WarmupInvalidZero_Legitimate()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(3), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("INVALID")),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, Array.Empty<G1TimedSample>()));
        Assert.True(r.EvidenceValid);
        Assert.Empty(r.ParentSamples);
        Assert.Null(r.Envelope!.WallSeconds);
    }

    [Fact]
    public async Task TimedInvalidComplete_WithConfirmedInvalid()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(3), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("INVALID", wall: 10.0)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0), Sample(1, "INVALID", card: 9), Sample(2) }));
        Assert.True(r.EvidenceValid);
        Assert.Equal(TimedResultStatus.Invalid, r.ParentSamples[1].Status);
        Assert.All(new[] { r.ParentSamples[0], r.ParentSamples[2] }, ps => Assert.Equal(TimedResultStatus.Valid, ps.Status));
    }

    [Fact]
    public async Task FalseValidClaim_IntegrityFailure()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 1.0)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0, digest: "wrong") }));
        Assert.False(r.EvidenceValid);
        Assert.Empty(r.ParentSamples);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("digest mismatch"));
    }

    [Fact]
    public async Task FalseInvalidClaim_IntegrityFailure()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("INVALID", wall: 1.0)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0, "INVALID") }));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("equals expected"));
    }

    [Fact]
    public async Task EnvelopeStatusMismatch_And_RepetitionMismatch_Fail()
    {
        using var s1 = NewSession();
        var badStatus = Envelope("VALID");
        badStatus.Status = LogicalStatus.Invalid;
        var r1 = await Run(s1, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, badStatus),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0) }));
        Assert.False(r1.EvidenceValid);

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID", wall: 1.0, repetition: 2)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0) }));
        Assert.False(r2.EvidenceValid);
    }

    [Fact]
    public async Task EnvelopeResultCardinalityNonnull_Fails()
    {
        using var s = NewSession();
        var env = Envelope("VALID", wall: 1.0);
        env.ResultCardinality = 5;
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, env),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0) }));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("must always carry null ResultCardinality"));
    }

    [Fact]
    public async Task MissingSampleAfterExit0_Fails()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID", wall: 1.0)));
        Assert.False(r.EvidenceValid);
        Assert.False(r.RegisteredStableArtifacts);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("missing G1 sample artifact"));
    }

    [Fact]
    public async Task TimeoutForensics_ObservedRegisters_NotObservedSkips()
    {
        using var s1 = NewSession();
        var r1 = await Run(s1, Workload(1), TimeoutProcess(wrapperObserved: true),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0) }));
        Assert.False(r1.EvidenceValid);
        Assert.Empty(r1.ParentSamples);
        Assert.Contains(SampleRel, Artifacts(s1)); // stable forensic capture

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(1), TimeoutProcess(wrapperObserved: false),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0) }));
        Assert.False(r2.EvidenceValid);
        Assert.DoesNotContain(SampleRel, Artifacts(s2)); // active file never snapshotted
    }

    [Fact]
    public async Task ResourceOrthogonal_ErrorDoesNotCorruptEvidence()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 1.0)),
            ResourceMeasurementStatus.Error, createResourceFile: false,
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0) }));
        Assert.True(r.EvidenceValid);
        Assert.Equal(ResourceMeasurementStatus.Error, r.ResourceStatus);
        Assert.Null(r.ExternalPeakRssBytes);
    }

    [Fact]
    public async Task ResourceValid_MissingRawFile_Fails()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 1.0)),
            createResourceFile: false,
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0) }));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("raw resource output file missing"));
    }

    [Fact]
    public async Task IdentityMismatch_FailsBeforeChildExecution()
    {
        using var s = NewSession(candidateId: "not-sqlite-native-v1");
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID", wall: 1.0)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0) }));
        Assert.Equal(ProcessOutcome.ParentError, r.ProcessOutcome);
        Assert.False(r.EvidenceValid);
        Assert.Empty(Artifacts(s));
    }

    [Fact]
    public async Task DeterministicEvidence_AndNoPublication()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult,
            Envelope("VALID", wall: 10.0)),
            sampleProducer: p => G1SampleArtifact.WriteCreateNew(p, new[] { Sample(0) }));
        Assert.True(r.EvidenceValid);
        string execution = File.ReadAllText(Physical(s, "graph/g1/rep-1/execution.json"));
        Assert.Contains("\"operation\":\"G1\"", execution);
        Assert.Contains("\"parent_samples\"", execution);
        Assert.Contains("\"watchdog_seconds\":3600", execution);
        string processJson = File.ReadAllText(Physical(s, "graph/g1/rep-1/process.json"));
        Assert.Contains("killAttempted", processJson);
        Assert.Contains("validationError", processJson);
    }
}

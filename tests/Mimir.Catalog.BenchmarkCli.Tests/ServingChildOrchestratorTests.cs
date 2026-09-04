using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class ServingChildOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-sco-" + Guid.NewGuid().ToString("N"));
    private const string Digest = "d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1";

    public ServingChildOrchestratorTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private RunIdentity Identity() => new()
    {
        EvidenceSchemaVersion = EvidenceSchema.Version,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = "run-1",
    };

    private ServingWorkload Workload(int count)
    {
        var probes = new List<ServingProbe>();
        var expected = new Dictionary<(string, long), ServingExpected>();
        for (long seq = 1; seq <= count; seq++)
        {
            probes.Add(new ServingProbe("S1", seq, "Hit", true, 100 + seq, null, null));
            expected[("S1", seq)] = new ServingExpected("S1", seq, true, 1, Digest);
        }
        return new ServingWorkload { Probes = probes, Expected = expected };
    }

    private ChildResultEnvelope Envelope(string correctness, LogicalStatus status, string? errorCategory = null, string? errorMessage = null) => new()
    {
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        WorkloadClass = WorkloadClass.Serving,
        Operation = "S1",
        Repetition = 1,
        Status = status,
        CorrectnessStatus = correctness,
        ErrorCategory = errorCategory,
        ErrorMessage = errorMessage,
    };

    private static ChildResultEnvelope Envelope(string correctness, string? errorCategory = null, string? errorMessage = null)
        => new()
        {
            ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
            CandidateId = CandidateAIdentity.CandidateId,
            CandidateConfigId = CandidateAIdentity.CandidateConfigId,
            WorkloadId = CandidateAIdentity.WorkloadId,
            CorpusId = CandidateAIdentity.CorpusId,
            WorkloadClass = WorkloadClass.Serving,
            Operation = "S1",
            Repetition = 1,
            Status = correctness switch { "VALID" => LogicalStatus.Valid, "INVALID" => LogicalStatus.Invalid, _ => LogicalStatus.Error },
            CorrectnessStatus = correctness,
            ErrorCategory = errorCategory,
            ErrorMessage = errorMessage,
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

    private static ServingTimedSample Sample(long seq, string status = "VALID", double wall = 0.5, long card = 1, string? digest = null, string? error = null)
        => new("S1", seq, "Hit", wall, status, card, digest ?? Digest, error);

    private EvidenceStagingSession NewSession()
    {
        string runs = Path.Combine(_root, "runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runs);
        return EvidenceStagingSession.Create(runs, Identity());
    }

    private string ResourcePhysical(EvidenceStagingSession session)
        => Path.Combine(session.StagingPath, "serving", "S1", "rep-1", "resource-time.txt");

    private async Task<ServingChildEvidenceResult> Run(
        EvidenceStagingSession session,
        ServingWorkload workload,
        ChildProcessResult process,
        ResourceMeasurementStatus resourceStatus = ResourceMeasurementStatus.Valid,
        bool createResourceFile = true,
        Action<string>? sampleProducer = null)
    {
        string resourcePhysical = ResourcePhysical(session);
        long? rss = resourceStatus == ResourceMeasurementStatus.Valid ? 1234L : null;
        return await ServingChildOrchestrator.RunAsync(
            session, "S1", 1, "/fixture/candidate.db", "/fixture/workload", workload,
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

    private string SamplePath(EvidenceStagingSession session)
        => Path.Combine(session.StagingPath, "serving", "S1", "rep-1", "request.serving-samples.jsonl");

    [Fact]
    public async Task Valid_Under5_And_Exactly5_Timeout()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, wall: 4.999) }));
        Assert.True(r.EvidenceValid);
        Assert.True(r.RegisteredStableArtifacts);
        Assert.Equal(TimedResultStatus.Valid, r.ParentSamples[0].Status);
        Assert.True(r.MeasuredSequenceComplete);

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, wall: 5.0) }));
        Assert.True(r2.EvidenceValid);
        Assert.Equal(TimedResultStatus.Timeout, r2.ParentSamples[0].Status);
    }

    [Fact]
    public async Task InvalidAndError_At6_StaySame()
    {
        using var s1 = NewSession();
        var r1 = await Run(s1, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("INVALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "INVALID", 6.0, card: 0, digest: "other") }));
        Assert.Equal(TimedResultStatus.Invalid, r1.ParentSamples[0].Status);

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "timed-probe", "boom")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "ERROR", 6.0, error: "boom") }));
        Assert.Equal(TimedResultStatus.Error, r2.ParentSamples[0].Status);
    }

    [Fact]
    public async Task FalseClaims_IntegrityFailures()
    {
        using var s1 = NewSession();
        var r1 = await Run(s1, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, digest: "wrong") }));
        Assert.False(r1.EvidenceValid);
        Assert.Empty(r1.ParentSamples);

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("INVALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "INVALID") }));
        Assert.False(r2.EvidenceValid);
    }

    [Fact]
    public async Task Envelope_StatusVsCorrectnessMismatch_Fails()
    {
        using var s = NewSession();
        var env = Envelope("VALID", LogicalStatus.Invalid);
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, env),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("inconsistent with CorrectnessStatus"));
    }

    [Fact]
    public async Task Error_ZeroSample_OnlyWarmupRuntime()
    {
        using var s1 = NewSession();
        var r1 = await Run(s1, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "warmup", "warm")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, Array.Empty<ServingTimedSample>()));
        Assert.True(r1.EvidenceValid);

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "timed-probe", "boom")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, Array.Empty<ServingTimedSample>()));
        Assert.False(r2.EvidenceValid);
    }

    [Fact]
    public async Task TimedErrorPrefix_WrongCategoryOrMessage_Fails()
    {
        using var s1 = NewSession();
        var r1 = await Run(s1, Workload(2), Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "tail", "boom")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1), Sample(2, "ERROR", error: "boom") }));
        Assert.False(r1.EvidenceValid);

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "timed-probe", "other")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "ERROR", error: "boom") }));
        Assert.False(r2.EvidenceValid);
        Assert.Contains(r2.EvidenceProblems, x => x.Contains("message must match"));
    }

    [Fact]
    public async Task S1TailError_WithPriorInvalid_Legitimate()
    {
        using var s = NewSession();
        var env = Envelope("ERROR", "tail", "tail-boom");
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, env),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "INVALID", card: 0, digest: "other") }));
        Assert.True(r.EvidenceValid);
        Assert.Equal(TimedResultStatus.Invalid, r.ParentSamples[0].Status);
    }

    [Fact]
    public async Task ReorderedFullCount_IncompleteAndInvalid()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(2), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(2), Sample(1) }));
        Assert.False(r.EvidenceValid);
        Assert.False(r.MeasuredSequenceComplete);
    }

    [Fact]
    public async Task ForensicCapture_CrashAndTimeoutObserved_NotActive()
    {
        // ProtocolResultError + wrapper observed + sample exists -> registered forensics, no samples.
        using var s1 = NewSession();
        string sampleRel = "serving/S1/rep-1/request.serving-samples.jsonl";
        var r1 = await Run(s1, Workload(1), Process(ProcessOutcome.ProtocolResultError, env: null),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.False(r1.EvidenceValid);
        Assert.Empty(r1.ParentSamples);
        Assert.Contains(sessionArtifacts(s1), rel => rel == sampleRel);

        // Timeout observed -> forensic sample registered.
        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(1), TimeoutProcess(wrapperObserved: true),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.False(r2.EvidenceValid);
        Assert.Contains(sessionArtifacts(s2), rel => rel == sampleRel);

        // Timeout not observed -> active sample never registered.
        using var s3 = NewSession();
        await Run(s3, Workload(1), TimeoutProcess(wrapperObserved: false),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.DoesNotContain(sessionArtifacts(s3), rel => rel == sampleRel);
    }

    private IReadOnlyList<string> sessionArtifacts(EvidenceStagingSession session)
        => session.RegisteredArtifacts.Select(a => a.RelativePath).ToList();

    [Fact]
    public async Task ResourceValid_MissingRawFile_IntegrityFailure()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")),
            createResourceFile: false, sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("raw resource output file missing"));
    }

    [Fact]
    public async Task ArtifactWriteFailure_RegisteredStableFalse()
    {
        using var s = NewSession();
        string execPhysical = Path.Combine(s.StagingPath, "serving", "S1", "rep-1", "execution.json");
        Directory.CreateDirectory(Path.GetDirectoryName(execPhysical)!);
        File.WriteAllText(execPhysical, "occupied"); // makes WriteOwned(execution.json) fail create-new
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.False(r.EvidenceValid);
        Assert.False(r.RegisteredStableArtifacts);
    }

    [Fact]
    public async Task MalformedButRegisteredSample_StableTrue_Invalid()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")),
            sampleProducer: p => File.WriteAllBytes(p, System.Text.Encoding.UTF8.GetBytes("{\"sequence\":1}\n")));
        Assert.False(r.EvidenceValid);
        Assert.True(r.RegisteredStableArtifacts);
    }

    [Fact]
    public async Task DiagnosticsPersisted()
    {
        using var s = NewSession();
        var r = await Run(s, Workload(1), Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.True(r.EvidenceValid);
        string processJson = File.ReadAllText(Path.Combine(s.StagingPath, "serving", "S1", "rep-1", "process.json"));
        Assert.Contains("wrapperExitObserved", processJson);
        Assert.Contains("killAttempted", processJson);
        Assert.Contains("validationError", processJson);
        string executionJson = File.ReadAllText(Path.Combine(s.StagingPath, "serving", "S1", "rep-1", "execution.json"));
        Assert.Contains("evidence_problems", executionJson);
        Assert.Contains("parent_samples", executionJson);
        Assert.Contains("watchdog_seconds", executionJson);
    }
    [Fact]
    public async Task InvalidEnvelope_NeverContainsMeasuredError()
    {
        using var s1 = NewSession();
        var r1 = await Run(s1, Workload(2), Process(ProcessOutcome.CompletedProtocolResult, Envelope("INVALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "INVALID", card: 0, digest: "other"), Sample(2, "ERROR", error: "boom") }));
        Assert.False(r1.EvidenceValid);
        Assert.Contains(r1.EvidenceProblems, x => x.Contains("INVALID envelope must never contain measured ERROR"));

        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(2), Process(ProcessOutcome.CompletedProtocolResult, Envelope("INVALID")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "INVALID", card: 0, digest: "other") }));
        Assert.False(r2.EvidenceValid); // partial INVALID sequence impossible
    }

    [Fact]
    public async Task TimedError_MustBeSingleFinalSample()
    {
        // ERROR followed by a later sample -> invalid
        using var s1 = NewSession();
        var r1 = await Run(s1, Workload(3), Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "timed-probe", "boom")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1), Sample(2, "ERROR", error: "boom"), Sample(3) }));
        Assert.False(r1.EvidenceValid);

        // Multiple ERROR samples -> invalid
        using var s2 = NewSession();
        var r2 = await Run(s2, Workload(2), Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "timed-probe", "boom")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "ERROR", error: "boom"), Sample(2, "ERROR", error: "boom") }));
        Assert.False(r2.EvidenceValid);

        // Earlier INVALID + final ERROR -> valid
        using var s3 = NewSession();
        var r3 = await Run(s3, Workload(2), Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "timed-probe", "boom")),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "INVALID", card: 0, digest: "other"), Sample(2, "ERROR", error: "boom") }));
        Assert.True(r3.EvidenceValid);
    }

    [Fact]
    public async Task ActiveResourceValid_Unstable_FailsClosed()
    {
        using var s = NewSession();
        string resourceRel = "serving/S1/rep-1/resource-time.txt";
        var r = await Run(s, Workload(1), TimeoutProcess(wrapperObserved: false),
            sampleProducer: p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.False(r.EvidenceValid);
        Assert.False(r.RegisteredStableArtifacts);
        Assert.DoesNotContain(sessionArtifacts(s), rel => rel == resourceRel);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("not stable"));
    }

    [Fact]
    public async Task StableResource_RegistrationFailure_CaptureFalse()
    {
        using var s = NewSession();
        string resourceRel = "serving/S1/rep-1/resource-time.txt";
        string resourcePhysical = ResourcePhysical(s);
        var r = await ServingChildOrchestrator.RunAsync(
            s, "S1", 1, "/fixture/candidate.db", "/fixture/workload", Workload(1),
            reqPath => ProcessInvocation.BenchmarkChild("/fixture/child.exe", reqPath),
            TimeSpan.FromSeconds(3600),
            (_, rp, _, _) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(rp)!);
                File.WriteAllText(rp, "1  maximum resident set size\n");
                s.RegisterExisting(resourceRel); // pre-register to force duplicate on orchestrator attempt
                return Task.FromResult(new ResourceMeasuredProcessResult
                {
                    ProcessResult = Process(ProcessOutcome.ProcessCrashOrNonzeroExit, env: null, exitCode: 3),
                    ResourceStatus = ResourceMeasurementStatus.Error,
                    ExternalPeakRssBytes = null,
                    ResourceOutputPath = resourcePhysical,
                });
            });
        Assert.False(r.EvidenceValid);
        Assert.False(r.RegisteredStableArtifacts);
    }
}


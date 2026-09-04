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

    private ChildResultEnvelope Envelope(string correctness, string? errorCategory = null, string? errorMessage = null) => new()
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
            WrapperExitObserved = true,
            OutputDrainCompleted = true,
            ParsedChildResult = env,
        };

    private static ResourceMeasuredProcessResult Resource(ChildProcessResult process, ResourceMeasurementStatus status = ResourceMeasurementStatus.Valid)
        => new()
        {
            ProcessResult = process,
            ResourceStatus = status,
            ExternalPeakRssBytes = status == ResourceMeasurementStatus.Valid ? 1234L : null,
            ResourceOutputPath = "/unused",
        };

    private async Task<ServingChildEvidenceResult> Run(
        EvidenceStagingSession session,
        ServingWorkload workload,
        ResourceMeasuredProcessResult resource,
        Action<string>? sampleProducer = null)
    {
        return await ServingChildOrchestrator.RunAsync(
            session, "S1", 1, "/fixture/candidate.db", "/fixture/workload", workload,
            reqPath => ProcessInvocation.BenchmarkChild("/fixture/child.exe", reqPath),
            TimeSpan.FromSeconds(3600),
            (_, _, _, _) => Task.FromResult(resource),
            sampleProducer).ConfigureAwait(false);
    }

    private string SamplePath(EvidenceStagingSession session)
        => Path.Combine(session.StagingPath, "serving", "S1", "rep-1", "request.serving-samples.jsonl");

    private static ServingTimedSample Sample(long seq, string status = "VALID", double wall = 0.5, long card = 1, string? digest = null, string? error = null)
        => new("S1", seq, "Hit", wall, status, card, digest ?? Digest, error);

    private EvidenceStagingSession NewSession()
    {
        string runs = Path.Combine(_root, "runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runs);
        return EvidenceStagingSession.Create(runs, Identity());
    }

    [Fact]
    public async Task Valid_Under5_ParentValid()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")));
        var r = await Run(session, Workload(1), resource, p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, wall: 4.999) }));
        Assert.True(r.EvidenceValid);
        Assert.Single(r.ParentSamples);
        Assert.Equal(TimedResultStatus.Valid, r.ParentSamples[0].Status);
        Assert.True(r.MeasuredSequenceComplete);
    }

    [Fact]
    public async Task Valid_Exactly5_Timeout()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")));
        var r = await Run(session, Workload(1), resource, p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, wall: 5.0) }));
        Assert.True(r.EvidenceValid);
        Assert.Equal(TimedResultStatus.Timeout, r.ParentSamples[0].Status);
    }

    [Fact]
    public async Task InvalidAt6_StaysInvalid_ErrorAt6_StaysError()
    {
        using (var s1 = NewSession())
        {
            var res = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("INVALID")));
            var r1 = await Run(s1, Workload(1), res, p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "INVALID", 6.0, card: 0, digest: "other") }));
            Assert.True(r1.EvidenceValid);
            Assert.Equal(TimedResultStatus.Invalid, r1.ParentSamples[0].Status);
        }
        using (var s2 = NewSession())
        {
            var res = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "timed-probe", "boom")));
            var r2 = await Run(s2, Workload(1), res, p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "ERROR", 6.0, error: "boom") }));
            Assert.True(r2.EvidenceValid);
            Assert.Equal(TimedResultStatus.Error, r2.ParentSamples[0].Status);
        }
    }

    [Fact]
    public async Task FalseValidClaim_IntegrityFailure()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")));
        var r = await Run(session, Workload(1), resource, p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, digest: "wrong") }));
        Assert.False(r.EvidenceValid);
        Assert.Empty(r.ParentSamples);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("digest mismatch"));
    }

    [Fact]
    public async Task FalseInvalidClaim_IntegrityFailure()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("INVALID")));
        var r = await Run(session, Workload(1), resource, p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1, "INVALID", card: 1, digest: Digest) }));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("equals expected"));
    }

    [Fact]
    public async Task MissingSampleAfterExit0_IntegrityFailure()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")));
        var r = await Run(session, Workload(1), resource); // no producer -> file absent
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("missing serving sample artifact"));
    }

    [Fact]
    public async Task ValidEnvelope_IncompleteSamples_Rejected()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")));
        var r = await Run(session, Workload(2), resource, p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.False(r.EvidenceValid);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("VALID envelope requires complete measured sequence"));
    }

    [Fact]
    public async Task WarmupInvalid_ZeroSamples_Legitimate()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("INVALID")));
        var r = await Run(session, Workload(1), resource, p => ServingSampleArtifact.WriteCreateNew(p, Array.Empty<ServingTimedSample>()));
        Assert.True(r.EvidenceValid);
        Assert.Empty(r.ParentSamples);
    }

    [Fact]
    public async Task TimedErrorPrefix_Legitimate()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("ERROR", "timed-probe", "boom")));
        var r = await Run(session, Workload(2), resource,
            p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1), Sample(2, "ERROR", error: "boom") }));
        Assert.True(r.EvidenceValid);
        Assert.Equal(2, r.ParentSamples.Count);
        Assert.Equal(TimedResultStatus.Valid, r.ParentSamples[0].Status);
        Assert.Equal(TimedResultStatus.Error, r.ParentSamples[1].Status);
    }

    [Fact]
    public async Task Watchdog_Incomplete_NoFabricatedSamples()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.Timeout, env: null), ResourceMeasurementStatus.Unavailable);
        var r = await Run(session, Workload(1), resource);
        Assert.False(r.EvidenceValid);
        Assert.Empty(r.ParentSamples);
        Assert.Contains(r.EvidenceProblems, x => x.Contains("process outcome Timeout"));
    }

    [Fact]
    public async Task Nonzero_Incomplete_NoFabricatedLogicalResult()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.ProcessCrashOrNonzeroExit, env: null, exitCode: 3));
        var r = await Run(session, Workload(1), resource);
        Assert.False(r.EvidenceValid);
        Assert.Null(r.Envelope);
    }

    [Fact]
    public async Task ResourceStatus_Orthogonal_AndArtifactsRegistered()
    {
        using var session = NewSession();
        var resource = Resource(Process(ProcessOutcome.CompletedProtocolResult, Envelope("VALID")), ResourceMeasurementStatus.Error);
        var r = await Run(session, Workload(1), resource, p => ServingSampleArtifact.WriteCreateNew(p, new[] { Sample(1) }));
        Assert.True(r.EvidenceValid); // resource Error does not corrupt benchmark evidence
        Assert.Equal(ResourceMeasurementStatus.Error, r.ResourceStatus);
        Assert.Null(r.ExternalPeakRssBytes);
        var rels = session.RegisteredArtifacts.Select(a => a.RelativePath).ToList();
        Assert.Contains("serving/S1/rep-1/request.json", rels);
        Assert.Contains("serving/S1/rep-1/request.serving-samples.jsonl", rels);
        Assert.Contains("serving/S1/rep-1/execution.json", rels);
        Assert.Contains("serving/S1/rep-1/process.json", rels);
    }
}

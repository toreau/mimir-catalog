using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;

namespace Mimir.Catalog.BenchmarkCli;

public sealed class G1ChildEvidenceResult
{
    public required string Operation { get; init; }
    public required int Repetition { get; init; }
    public required ProcessOutcome ProcessOutcome { get; init; }
    public required ResourceMeasurementStatus ResourceStatus { get; init; }
    public long? ExternalPeakRssBytes { get; init; }
    public ChildResultEnvelope? Envelope { get; init; }
    public required IReadOnlyList<G1ParentSample> ParentSamples { get; init; }
    public required bool MeasuredSequenceComplete { get; init; }
    public required bool EvidenceValid { get; init; }
    public required IReadOnlyList<string> EvidenceProblems { get; init; }
    public required double WatchdogSeconds { get; init; }
    public required bool RegisteredStableArtifacts { get; init; }
}

/// <summary>
/// One-child G1 parent orchestrator. Writes request evidence into a caller-owned
/// EvidenceStagingSession, launches one resource-wrapped child, strictly parses
/// and independently validates the raw G1 sample artifact, derives parent timed
/// statuses and writes deterministic process/execution evidence. Never
/// finalizes/promotes; never runs the repetition coordinator.
/// </summary>
public static class G1ChildOrchestrator
{
    internal static string ExecutionDir(int repetition) => $"graph/g1/rep-{repetition}";

    public static async Task<G1ChildEvidenceResult> RunAsync(
        EvidenceStagingSession session,
        int repetition,
        string candidatePath,
        string workloadPath,
        GraphWorkload workload,
        Func<string, ProcessInvocation> childInvocationFactory,
        TimeSpan watchdog,
        Func<ProcessInvocation, string, TimeSpan, ChildRequestEnvelope, Task<ResourceMeasuredProcessResult>>? resourceRunner = null,
        Action<string>? sampleProducer = null)
    {
        resourceRunner ??= (invocation, resourcePath, timeout, request) =>
            ResourceMeasuredChildRunner.RunAsync(invocation, resourcePath, timeout, request);

        string identityProblem = IdentityProblem(session.Identity);
        if (identityProblem is not null)
            return FailureResult(repetition, watchdog, identityProblem);
        if (repetition is < 1 or > 3)
            return FailureResult(repetition, watchdog, $"invalid repetition {repetition}");

        string dir = ExecutionDir(repetition);
        string requestRel = $"{dir}/request.json";
        string sampleRel = $"{dir}/request.g1-samples.jsonl";
        string stdoutRel = $"{dir}/stdout.txt";
        string stderrRel = $"{dir}/stderr.txt";
        string processRel = $"{dir}/process.json";
        string resourceRel = $"{dir}/resource-time.txt";
        string executionRel = $"{dir}/execution.json";
        string physical(string rel) => Path.Combine(session.StagingPath, rel.Replace('/', Path.DirectorySeparatorChar));

        string requestPhysical = physical(requestRel);
        string samplePhysical = physical(sampleRel);
        string resourcePhysical = physical(resourceRel);
        if (File.Exists(samplePhysical) || Directory.Exists(samplePhysical))
            return FailureResult(repetition, watchdog, $"child sample path already exists: {sampleRel}");
        if (File.Exists(requestPhysical))
            return FailureResult(repetition, watchdog, $"request path already exists: {requestRel}");
        if (File.Exists(resourcePhysical))
            return FailureResult(repetition, watchdog, $"resource path already exists: {resourceRel}");

        var envelope = BuildRequest(session.Identity, repetition, candidatePath, workloadPath);
        try { session.WriteText(requestRel, ProtocolJson.ToJson(envelope)); }
        catch (Exception ex) { return FailureResult(repetition, watchdog, $"failed to write request evidence: {ex.Message}"); }

        ProcessInvocation inner = childInvocationFactory(requestPhysical);
        ResourceMeasuredProcessResult resource;
        try { resource = await resourceRunner(inner, resourcePhysical, watchdog, envelope).ConfigureAwait(false); }
        catch (Exception ex) { return FailureResult(repetition, watchdog, $"resource-wrapped child execution failed: {ex.Message}"); }

        var process = resource.ProcessResult;
        sampleProducer?.Invoke(samplePhysical);
        var problems = new List<string>();
        var parentSamples = new List<G1ParentSample>();
        bool exactSequence = false;
        bool trusted = process.Outcome == ProcessOutcome.CompletedProtocolResult && !process.TimedOut;
        bool captureOk = true;

        if (trusted)
        {
            var env = process.ParsedChildResult;
            if (env is null)
            {
                problems.Add("completed child produced no parsed envelope");
            }
            else
            {
                var measured = workload.Probes;
                List<G1TimedSample> raw = new();
                if (!File.Exists(samplePhysical))
                {
                    problems.Add("missing G1 sample artifact after trustworthy exit-0 envelope");
                    captureOk = false;
                }
                else
                {
                    try { session.RegisterExisting(sampleRel); }
                    catch (Exception ex) { problems.Add($"failed to register child sample artifact: {ex.Message}"); captureOk = false; }
                    try { raw.AddRange(G1SampleParser.Parse(File.ReadAllBytes(samplePhysical))); }
                    catch (G1SampleParseException ex) { problems.Add($"malformed G1 sample artifact: {ex.Message}"); }
                }

                bool envMapping = ValidateEnvelopeMapping(env, repetition, problems);
                bool seqOk = ValidateSequenceExact(measured, raw, problems);
                exactSequence = seqOk && raw.Count == measured.Count;
                bool formsOk = ValidateEnvelopeForms(measured, raw, env, problems);

                if (problems.Count == 0 && envMapping && seqOk && formsOk)
                {
                    foreach (var sample in raw)
                    {
                        var exp = workload.Expected[("G1", sample.Sequence)];
                        var claimProblems = G1ParentClassifier.VerifyClaim(sample, exp);
                        if (claimProblems.Count > 0) { problems.AddRange(claimProblems); continue; }
                        parentSamples.Add(new G1ParentSample(
                            sample.Operation, sample.Sequence, sample.Stratum, sample.WallSeconds,
                            G1ParentClassifier.PointStatus(sample.CorrectnessStatus, sample.WallSeconds),
                            sample.CorrectnessStatus, sample.ActualCardinality, sample.ActualVisited, sample.ActualDigest, sample.Error));
                    }
                }
            }
        }
        else
        {
            problems.Add($"process outcome {process.Outcome} is not a trustworthy completed result");
            if (process.WrapperExitObserved && File.Exists(samplePhysical) && !Directory.Exists(samplePhysical))
            {
                try { session.RegisterExisting(sampleRel); }
                catch (Exception ex) { problems.Add($"failed to register forensic sample artifact: {ex.Message}"); captureOk = false; }
            }
        }

        bool resourceStable = !process.TimedOut || process.WrapperExitObserved;
        if (resource.ResourceStatus == ResourceMeasurementStatus.Valid)
        {
            if (!resourceStable)
            {
                problems.Add("resource status Valid but resource output is not stable (timeout, wrapper not observed)");
                captureOk = false;
            }
            else
            {
                if (resource.ExternalPeakRssBytes is null)
                    problems.Add("resource status Valid but no external peak RSS bytes");
                if (string.IsNullOrEmpty(resource.ResourceOutputPath) || !EvidencePathSafety.IsSamePath(resource.ResourceOutputPath, resourcePhysical))
                    problems.Add("resource output path does not match this execution's supplied path");
                if (!File.Exists(resourcePhysical))
                {
                    problems.Add("resource status Valid but raw resource output file missing");
                    captureOk = false;
                }
                else
                {
                    try { session.RegisterExisting(resourceRel); }
                    catch (Exception ex) { problems.Add($"failed to register resource output: {ex.Message}"); captureOk = false; }
                }
            }
        }
        else if (resourceStable && File.Exists(resourcePhysical))
        {
            try { session.RegisterExisting(resourceRel); }
            catch (Exception ex) { problems.Add($"failed to register resource output: {ex.Message}"); captureOk = false; }
        }

        bool ownedOk = true;
        ownedOk &= WriteOwned(session, stdoutRel, process.Stdout ?? "", problems);
        ownedOk &= WriteOwned(session, stderrRel, process.Stderr ?? "", problems);
        ownedOk &= WriteOwned(session, processRel, JsonOf(new
        {
            outcome = process.Outcome.ToString(),
            timedOut = process.TimedOut,
            exitCode = process.ExitCode,
            elapsedParentWallSeconds = process.ElapsedParentWallSeconds,
            killAttempted = process.KillAttempted,
            killCallSucceeded = process.KillCallSucceeded,
            killError = process.KillError,
            wrapperExitObserved = process.WrapperExitObserved,
            descendantTerminationVerified = process.DescendantTerminationVerified,
            outputDrainCompleted = process.OutputDrainCompleted,
            cleanupError = process.CleanupError,
            validationError = process.ValidationError,
        }), problems);
        ownedOk &= WriteOwned(session, executionRel, JsonOf(new
        {
            operation = "G1",
            repetition,
            watchdog_seconds = watchdog.TotalSeconds,
            evidence_valid = problems.Count == 0,
            evidence_problems = problems,
            measured_sequence_complete = exactSequence,
            resource_status = resource.ResourceStatus.ToString(),
            external_peak_rss_bytes = resource.ExternalPeakRssBytes,
            resource_error = resource.ResourceError,
            sample_count = parentSamples.Count,
            parent_samples = parentSamples.Select(s => new
            {
                operation = s.Operation,
                sequence = s.Sequence,
                stratum = s.Stratum,
                wall_seconds = s.WallSeconds,
                status = s.Status.ToString(),
                child_correctness = s.ChildCorrectness,
            }),
        }), problems);

        return new G1ChildEvidenceResult
        {
            Operation = "G1",
            Repetition = repetition,
            ProcessOutcome = process.Outcome,
            ResourceStatus = resource.ResourceStatus,
            ExternalPeakRssBytes = resource.ExternalPeakRssBytes,
            Envelope = process.ParsedChildResult,
            ParentSamples = parentSamples,
            MeasuredSequenceComplete = exactSequence,
            EvidenceValid = problems.Count == 0,
            EvidenceProblems = problems,
            WatchdogSeconds = watchdog.TotalSeconds,
            RegisteredStableArtifacts = captureOk && ownedOk,
        };
    }

    private static G1ChildEvidenceResult FailureResult(int repetition, TimeSpan watchdog, string reason)
        => new()
        {
            Operation = "G1",
            Repetition = repetition,
            ProcessOutcome = ProcessOutcome.ParentError,
            ResourceStatus = ResourceMeasurementStatus.Unavailable,
            ParentSamples = Array.Empty<G1ParentSample>(),
            MeasuredSequenceComplete = false,
            EvidenceValid = false,
            EvidenceProblems = new[] { reason },
            WatchdogSeconds = watchdog.TotalSeconds,
            RegisteredStableArtifacts = false,
        };

    private static bool WriteOwned(EvidenceStagingSession session, string rel, string text, List<string> problems)
    {
        try { session.WriteText(rel, text); return true; }
        catch (Exception ex) { problems.Add($"failed to write {rel}: {ex.Message}"); return false; }
    }

    private static string JsonOf(object value) => System.Text.Json.JsonSerializer.Serialize(value, value.GetType(),
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    private static ChildRequestEnvelope BuildRequest(RunIdentity identity, int repetition, string candidatePath, string workloadPath)
        => new()
        {
            ProtocolVersion = identity.ProtocolVersion,
            CandidateId = identity.CandidateId,
            CandidateConfigId = identity.CandidateConfigId,
            WorkloadId = identity.WorkloadId,
            CorpusId = identity.CorpusId,
            WorkloadClass = WorkloadClass.G1,
            Operation = "G1",
            Repetition = repetition,
            CandidatePath = candidatePath,
            WorkloadPath = workloadPath,
            RunId = identity.RunId,
        };

    private static string? IdentityProblem(RunIdentity id)
    {
        if (id.ProtocolVersion != ProtocolConstants.ChildProtocolVersion) return "protocol version mismatch";
        if (id.CandidateId != CandidateAIdentity.CandidateId) return "candidate id mismatch";
        if (id.CandidateConfigId != CandidateAIdentity.CandidateConfigId) return "candidate config id mismatch";
        if (id.WorkloadId != CandidateAIdentity.WorkloadId) return "workload id mismatch";
        if (id.CorpusId != CandidateAIdentity.CorpusId) return "corpus id mismatch";
        return null;
    }

    private static bool ValidateEnvelopeMapping(ChildResultEnvelope env, int repetition, List<string> problems)
    {
        bool ok = true;
        string expected = env.Status switch
        {
            LogicalStatus.Valid => "VALID",
            LogicalStatus.Invalid => "INVALID",
            LogicalStatus.Error => "ERROR",
            _ => null,
        };
        if (expected is null || env.CorrectnessStatus != expected)
        {
            problems.Add($"envelope Status {env.Status} inconsistent with CorrectnessStatus '{env.CorrectnessStatus}'");
            ok = false;
        }
        if (env.WorkloadClass != WorkloadClass.G1 || env.Operation != "G1" || env.Repetition != repetition)
        {
            problems.Add("envelope workload-class/operation/repetition does not match the G1 request");
            ok = false;
        }
        if (env.ResultCardinality is not null || env.ResultDigest is not null)
        {
            problems.Add("G1 envelope must always carry null ResultCardinality/ResultDigest");
            ok = false;
        }
        return ok;
    }

    private static bool ValidateSequenceExact(IReadOnlyList<GraphProbe> measured, IReadOnlyList<G1TimedSample> raw, List<string> problems)
    {
        bool ok = true;
        for (int i = 0; i < raw.Count; i++)
        {
            var s = raw[i];
            if (i >= measured.Count)
            {
                problems.Add($"sample list overlong at position {i}");
                ok = false;
                break;
            }
            var m = measured[i];
            if (s.Operation != m.Op || s.Sequence != m.Seq || s.Stratum != m.Stratum)
            {
                problems.Add($"sample at position {i} does not match measured sequence ({s.Operation},{s.Sequence},{s.Stratum}) vs ({m.Op},{m.Seq},{m.Stratum})");
                ok = false;
            }
        }
        return ok;
    }

    private static bool ValidateEnvelopeForms(
        IReadOnlyList<GraphProbe> measured,
        IReadOnlyList<G1TimedSample> raw,
        ChildResultEnvelope env,
        List<string> problems)
    {
        bool ok = true;
        string envStatus = env.CorrectnessStatus;
        var statuses = raw.Select(s => s.CorrectnessStatus).ToList();
        int errorCount = statuses.Count(s => s == ServingStatuses.Error);
        bool hasError = errorCount > 0;
        bool hasInvalid = statuses.Contains(ServingStatuses.Invalid);
        bool wallFinite = env.WallSeconds is { } w && double.IsFinite(w) && w >= 0;

        void Bad(string reason) { problems.Add(reason); ok = false; }

        if (envStatus == ServingStatuses.Valid)
        {
            if (env.ErrorCategory is not null || env.ErrorMessage is not null) Bad("VALID envelope must not carry ERROR diagnostics");
            if (raw.Count != measured.Count) Bad("VALID envelope requires a complete measured sequence");
            if (!wallFinite) Bad("VALID envelope requires finite non-negative WallSeconds");
            if (statuses.Any(s => s != ServingStatuses.Valid)) Bad("VALID envelope requires every sample correctness VALID");
        }
        else if (envStatus == ServingStatuses.Invalid)
        {
            if (env.ErrorCategory is not null || env.ErrorMessage is not null) Bad("INVALID envelope must not carry ERROR diagnostics");
            if (raw.Count == 0)
            {
                // warmup INVALID
                if (env.WallSeconds is not null) Bad("warmup INVALID envelope must have null WallSeconds");
            }
            else if (raw.Count == measured.Count && !hasError && hasInvalid)
            {
                // timed INVALID complete
                if (!wallFinite) Bad("timed INVALID envelope requires finite non-negative WallSeconds");
            }
            else
            {
                Bad("INVALID envelope not covered by a legitimate child form (zero warmup or complete timed without ERROR)");
            }
        }
        else if (envStatus == ServingStatuses.Error)
        {
            if (raw.Count == 0)
            {
                if (env.ErrorCategory is not ("warmup" or "runtime"))
                    Bad("ERROR zero-sample envelope requires ErrorCategory warmup|runtime");
                if (string.IsNullOrEmpty(env.ErrorMessage)) Bad("ERROR zero-sample envelope requires an ErrorMessage");
                if (env.WallSeconds is not null) Bad("zero-sample ERROR envelope must have null WallSeconds");
            }
            else
            {
                if (errorCount != 1 || raw[^1].CorrectnessStatus != ServingStatuses.Error)
                {
                    Bad("timed-start ERROR requires exactly one measured ERROR as the final sample");
                }
                else
                {
                    if (env.ErrorCategory != "timed-start") Bad("timed-start ERROR envelope requires ErrorCategory timed-start");
                    if (string.IsNullOrEmpty(env.ErrorMessage)) Bad("timed-start ERROR envelope requires an ErrorMessage");
                    if (raw[^1].Error is null || env.ErrorMessage != raw[^1].Error)
                        Bad("timed-start ERROR envelope message must match the final ERROR sample");
                    if (!wallFinite) Bad("timed-start ERROR envelope requires finite non-negative WallSeconds");
                }
                if (raw.Count > measured.Count) Bad("timed-start ERROR raw list overlong");
            }
        }
        return ok;
    }
}

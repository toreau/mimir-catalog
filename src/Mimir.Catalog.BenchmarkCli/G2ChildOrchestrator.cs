using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;

namespace Mimir.Catalog.BenchmarkCli;

public sealed class G2ChildEvidenceResult
{
    public required string Operation { get; init; }
    public required int Repetition { get; init; }
    public required ProcessOutcome ProcessOutcome { get; init; }
    public required ResourceMeasurementStatus ResourceStatus { get; init; }
    public long? ExternalPeakRssBytes { get; init; }
    public ChildResultEnvelope? Envelope { get; init; }
    public required IReadOnlyList<G2ParentPerInput> PerInput { get; init; }
    public G2ParentBatch? Batch { get; init; }
    public required bool TimedBatchComplete { get; init; }
    public required bool EvidenceValid { get; init; }
    public required IReadOnlyList<string> EvidenceProblems { get; init; }
    public required double WatchdogSeconds { get; init; }
    public required bool RegisteredStableArtifacts { get; init; }
}

/// <summary>
/// One-child G2 parent orchestrator. Writes request evidence into a caller-owned
/// EvidenceStagingSession, launches one resource-wrapped child, strictly parses
/// and independently validates the raw G2 result artifact (writer-exact), derives
/// the parent Batch timed status and writes deterministic process/execution
/// evidence. Never finalizes/promotes; never runs the repetition coordinator.
/// </summary>
public static class G2ChildOrchestrator
{
    internal static string ExecutionDir(int repetition) => $"graph/g2/rep-{repetition}";

    public static async Task<G2ChildEvidenceResult> RunAsync(
        EvidenceStagingSession session,
        int repetition,
        string candidatePath,
        string workloadPath,
        G2Workload workload,
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
        string sampleRel = $"{dir}/request.g2-results.jsonl";
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
        var parentPerInput = new List<G2ParentPerInput>();
        G2ParentBatch? parentBatch = null;
        bool timedComplete = false;
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
                G2RawDocument? doc = null;
                if (!File.Exists(samplePhysical))
                {
                    problems.Add("missing G2 result artifact after trustworthy exit-0 envelope");
                    captureOk = false;
                }
                else
                {
                    try { session.RegisterExisting(sampleRel); }
                    catch (Exception ex) { problems.Add($"failed to register child result artifact: {ex.Message}"); captureOk = false; }
                    try { doc = G2ResultParser.Parse(File.ReadAllBytes(samplePhysical)); }
                    catch (G2ResultParseException ex) { problems.Add($"malformed G2 result artifact: {ex.Message}"); }
                }

                bool envMapping = ValidateEnvelopeMapping(env, repetition, problems);
                bool structureOk = doc is not null && ValidateStructure(workload, doc!, out timedComplete, problems);
                bool formsOk = false;
                if (structureOk)
                    formsOk = ValidateEnvelopeForms(doc!, env, problems);

                if (problems.Count == 0 && envMapping && structureOk && formsOk)
                {
                    var confirmedChildStatuses = new List<string>();
                    foreach (var raw in doc!.PerInput)
                    {
                        var exp = workload.PerInput[raw.Item];
                        var claimProblems = G2ParentClassifier.VerifyPerInputClaim(raw, exp);
                        if (claimProblems.Count > 0) { problems.AddRange(claimProblems); continue; }
                        parentPerInput.Add(new G2ParentPerInput(raw.Item, raw.Qid, raw.SourceStratum, raw.CorrectnessStatus,
                            raw.ActualCardinality, raw.ActualDigest, raw.Error));
                        confirmedChildStatuses.Add(raw.CorrectnessStatus);
                    }
                    if (problems.Count == 0 && doc.Batch is not null)
                    {
                        var batchProblems = G2ParentClassifier.VerifyBatchClaim(doc.Batch, workload.Batch, confirmedChildStatuses);
                        if (batchProblems.Count > 0) { problems.AddRange(batchProblems); }
                        else
                        {
                            parentBatch = new G2ParentBatch(
                                doc.Batch.WallSeconds,
                                G2ParentClassifier.PointStatus(doc.Batch.CorrectnessStatus, doc.Batch.WallSeconds),
                                doc.Batch.CorrectnessStatus,
                                doc.Batch.ActualCardinality, doc.Batch.ActualDigest, doc.Batch.Error);
                        }
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
            operation = "G2",
            repetition,
            watchdog_seconds = watchdog.TotalSeconds,
            evidence_valid = problems.Count == 0,
            evidence_problems = problems,
            resource_status = resource.ResourceStatus.ToString(),
            external_peak_rss_bytes = resource.ExternalPeakRssBytes,
            resource_error = resource.ResourceError,
            timed_batch_complete = timedComplete,
            per_input_count = parentPerInput.Count,
            per_input_valid_count = parentPerInput.Count(p => p.ChildCorrectness == ServingStatuses.Valid),
            per_input_invalid_count = parentPerInput.Count(p => p.ChildCorrectness == ServingStatuses.Invalid),
            per_input_error_count = parentPerInput.Count(p => p.ChildCorrectness == ServingStatuses.Error),
            batch_present = parentBatch is not null,
            batch = parentBatch is null ? null : new
            {
                wall_seconds = parentBatch.WallSeconds,
                status = parentBatch.Status.ToString(),
                child_correctness = parentBatch.ChildCorrectness,
                actual_cardinality = parentBatch.ActualCardinality,
                actual_digest = parentBatch.ActualDigest,
                error = parentBatch.Error,
            },
        }), problems);

        return new G2ChildEvidenceResult
        {
            Operation = "G2",
            Repetition = repetition,
            ProcessOutcome = process.Outcome,
            ResourceStatus = resource.ResourceStatus,
            ExternalPeakRssBytes = resource.ExternalPeakRssBytes,
            Envelope = process.ParsedChildResult,
            PerInput = parentPerInput,
            Batch = parentBatch,
            TimedBatchComplete = timedComplete,
            EvidenceValid = problems.Count == 0,
            EvidenceProblems = problems,
            WatchdogSeconds = watchdog.TotalSeconds,
            RegisteredStableArtifacts = captureOk && ownedOk,
        };
    }

    private static G2ChildEvidenceResult FailureResult(int repetition, TimeSpan watchdog, string reason)
        => new()
        {
            Operation = "G2",
            Repetition = repetition,
            ProcessOutcome = ProcessOutcome.ParentError,
            ResourceStatus = ResourceMeasurementStatus.Unavailable,
            PerInput = Array.Empty<G2ParentPerInput>(),
            Batch = null,
            TimedBatchComplete = false,
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
            WorkloadClass = WorkloadClass.G2,
            Operation = "G2",
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
        if (env.WorkloadClass != WorkloadClass.G2 || env.Operation != "G2" || env.Repetition != repetition)
        {
            problems.Add("envelope workload-class/operation/repetition does not match the G2 request");
            ok = false;
        }
        return ok;
    }

    private static bool ValidateStructure(G2Workload workload, G2RawDocument doc, out bool timedComplete, List<string> problems)
    {
        bool ok = true;
        int expectedCount = workload.Concepts.Count;
        bool nPlusOne = doc.PerInput.Count == expectedCount && doc.Batch is not null;
        bool zero = doc.PerInput.Count == 0 && doc.Batch is null;
        timedComplete = nPlusOne;
        if (!nPlusOne && !zero)
        {
            problems.Add("raw evidence is neither a complete N+1 timed form nor a zero-record form");
            return false;
        }
        if (nPlusOne)
        {
            for (int i = 0; i < doc.PerInput.Count; i++)
            {
                var raw = doc.PerInput[i];
                var exp = workload.PerInput[i];
                if (raw.Item != i || raw.Qid != exp.Qid || raw.SourceStratum != exp.SourceStratum)
                {
                    problems.Add($"per-input at position {i} does not match workload ({raw.Item},{raw.Qid},{raw.SourceStratum}) vs expected item {i}");
                    ok = false;
                }
            }
        }
        return ok;
    }

    private static bool ValidateEnvelopeForms(G2RawDocument doc, ChildResultEnvelope env, List<string> problems)
    {
        bool ok = true;
        string envStatus = env.CorrectnessStatus;
        var rawBatch = doc.Batch;
        bool zero = doc.PerInput.Count == 0 && rawBatch is null;

        void Bad(string reason) { problems.Add(reason); ok = false; }

        // Cross-binding invariant for any timed evidence: the envelope
        // correctness claim must exactly equal the raw Batch correctness claim.
        if (rawBatch is not null && env.CorrectnessStatus != rawBatch.CorrectnessStatus)
        {
            problems.Add($"envelope CorrectnessStatus '{env.CorrectnessStatus}' does not match raw Batch correctness '{rawBatch.CorrectnessStatus}'");
            ok = false;
            return ok;
        }

        bool batchWallEquals(double? wall) => rawBatch is not null && wall is { } w && w == rawBatch.WallSeconds;
        bool resultEqualsRaw() => rawBatch is not null
            && env.ResultCardinality == rawBatch.ActualCardinality
            && env.ResultDigest == rawBatch.ActualDigest;

        if (envStatus == ServingStatuses.Valid)
        {
            if (env.ErrorCategory is not null || env.ErrorMessage is not null) Bad("VALID envelope must not carry ERROR diagnostics");
            if (zero) { Bad("VALID envelope requires complete timed evidence"); return ok; }
            if (!batchWallEquals(env.WallSeconds)) Bad("VALID envelope WallSeconds must exactly equal raw Batch wall");
            if (!resultEqualsRaw()) Bad("VALID envelope ResultCardinality/Digest must exactly equal raw Batch");
        }
        else if (envStatus == ServingStatuses.Invalid)
        {
            if (env.ErrorCategory is not null || env.ErrorMessage is not null) Bad("INVALID envelope must not carry ERROR diagnostics");
            if (zero)
            {
                if (env.WallSeconds is not null || env.ResultCardinality is not null || env.ResultDigest is not null)
                    Bad("warmup INVALID envelope must carry null wall/cardinality/digest");
            }
            else
            {
                if (!batchWallEquals(env.WallSeconds)) Bad("INVALID envelope WallSeconds must exactly equal raw Batch wall");
                if (!resultEqualsRaw()) Bad("INVALID envelope ResultCardinality/Digest must exactly equal raw Batch");
            }
        }
        else if (envStatus == ServingStatuses.Error)
        {
            if (zero)
            {
                if (env.ErrorCategory is not ("warmup" or "runtime")) Bad("ERROR zero-sample envelope requires ErrorCategory warmup|runtime");
                if (string.IsNullOrEmpty(env.ErrorMessage)) Bad("ERROR zero-sample envelope requires an ErrorMessage");
                if (env.WallSeconds is not null || env.ResultCardinality is not null || env.ResultDigest is not null)
                    Bad("ERROR zero-sample envelope must carry null wall/cardinality/digest");
            }
            else
            {
                if (env.ErrorCategory != "timed-batch") Bad("timed-batch ERROR envelope requires ErrorCategory timed-batch");
                if (string.IsNullOrEmpty(env.ErrorMessage)) Bad("timed-batch ERROR envelope requires an ErrorMessage");
                if (rawBatch is null) Bad("timed-batch ERROR envelope requires raw Batch evidence");
                if (rawBatch is not null && !batchWallEquals(env.WallSeconds)) Bad("timed-batch ERROR WallSeconds must exactly equal raw Batch wall");
                if (env.ResultCardinality is not null || env.ResultDigest is not null) Bad("timed-batch ERROR envelope must carry null card/digest");
                if (rawBatch is not null && (env.ErrorMessage != rawBatch.Error)) Bad("timed-batch ERROR envelope message must exactly equal raw Batch error");
            }
        }
        return ok;
    }
}

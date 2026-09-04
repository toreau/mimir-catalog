using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Process;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.BenchmarkCli.Resource;

namespace Mimir.Catalog.BenchmarkCli;

public sealed class ServingChildEvidenceResult
{
    public required string Operation { get; init; }
    public required int Repetition { get; init; }
    public required ProcessOutcome ProcessOutcome { get; init; }
    public required ResourceMeasurementStatus ResourceStatus { get; init; }
    public long? ExternalPeakRssBytes { get; init; }
    public ChildResultEnvelope? Envelope { get; init; }
    public required IReadOnlyList<ServingParentSample> ParentSamples { get; init; }
    public required bool MeasuredSequenceComplete { get; init; }
    public required bool EvidenceValid { get; init; }
    public required IReadOnlyList<string> EvidenceProblems { get; init; }
    public required double WatchdogSeconds { get; init; }
    public required bool RegisteredStableArtifacts { get; init; }
}

/// <summary>
/// One-child serving orchestrator. Writes request evidence into a caller-owned
/// EvidenceStagingSession, launches one resource-wrapped child, strictly parses
/// and independently validates the raw sample artifact, derives parent timed
/// statuses and writes deterministic execution/process evidence. Never
/// finalizes or promotes; never runs the 15-child loop.
/// </summary>
public static class ServingChildOrchestrator
{
    internal static string ExecutionDir(string operation, int repetition) => $"serving/{operation}/rep-{repetition}";

    public static async Task<ServingChildEvidenceResult> RunAsync(
        EvidenceStagingSession session,
        string operation,
        int repetition,
        string candidatePath,
        string workloadPath,
        ServingWorkload workload,
        Func<string, ProcessInvocation> childInvocationFactory,
        TimeSpan watchdog,
        Func<ProcessInvocation, string, TimeSpan, ChildRequestEnvelope, Task<ResourceMeasuredProcessResult>>? resourceRunner = null,
        Action<string>? sampleProducer = null)
    {
        resourceRunner ??= (invocation, resourcePath, timeout, request) =>
            ResourceMeasuredChildRunner.RunAsync(invocation, resourcePath, timeout, request);

        string identityProblem = IdentityProblem(session.Identity);
        if (identityProblem is not null)
            return FailureResult(operation, repetition, watchdog, identityProblem);
        if (operation is not ("S1" or "S2" or "S3" or "S4" or "S5") || repetition is < 1 or > 3)
            return FailureResult(operation, repetition, watchdog, $"invalid operation/repetition {operation}/{repetition}");

        string dir = ExecutionDir(operation, repetition);
        string requestRel = $"{dir}/request.json";
        string sampleRel = $"{dir}/request.serving-samples.jsonl";
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
            return FailureResult(operation, repetition, watchdog, $"child sample path already exists: {sampleRel}");
        if (File.Exists(requestPhysical))
            return FailureResult(operation, repetition, watchdog, $"request path already exists: {requestRel}");
        if (File.Exists(resourcePhysical))
            return FailureResult(operation, repetition, watchdog, $"resource path already exists: {resourceRel}");

        var envelope = BuildRequest(session.Identity, operation, repetition, candidatePath, workloadPath);
        try { session.WriteText(requestRel, ProtocolJson.ToJson(envelope)); }
        catch (Exception ex) { return FailureResult(operation, repetition, watchdog, $"failed to write request evidence: {ex.Message}"); }

        ProcessInvocation inner = childInvocationFactory(requestPhysical);
        ResourceMeasuredProcessResult resource;
        try { resource = await resourceRunner(inner, resourcePhysical, watchdog, envelope).ConfigureAwait(false); }
        catch (Exception ex) { return FailureResult(operation, repetition, watchdog, $"resource-wrapped child execution failed: {ex.Message}"); }

        var process = resource.ProcessResult;
        sampleProducer?.Invoke(samplePhysical);
        var problems = new List<string>();
        var parentSamples = new List<ServingParentSample>();
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
                var measured = ServingTimingRunner.Select(workload.Probes, operation, measuredOnly: true);
                List<ServingTimedSample> raw = new();
                if (!File.Exists(samplePhysical))
                {
                    problems.Add("missing serving sample artifact after trustworthy exit-0 envelope");
                    captureOk = false;
                }
                else
                {
                    try { session.RegisterExisting(sampleRel); }
                    catch (Exception ex) { problems.Add($"failed to register child sample artifact: {ex.Message}"); captureOk = false; }
                    try { raw.AddRange(ServingSampleParser.Parse(File.ReadAllBytes(samplePhysical))); }
                    catch (ServingSampleParseException ex) { problems.Add($"malformed serving sample artifact: {ex.Message}"); }
                }

                bool envMapping = ValidateEnvelopeMapping(env, problems);
                bool seqOk = ValidateSequenceExact(operation, measured, raw, problems);
                exactSequence = seqOk && raw.Count == measured.Count;
                bool formsOk = ValidateEnvelopeForms(operation, measured, raw, env, problems);

                if (problems.Count == 0 && envMapping && seqOk && formsOk)
                {
                    foreach (var sample in raw)
                    {
                        var exp = workload.Expected[(sample.Operation, sample.Sequence)];
                        var claimProblems = ServingParentClassifier.VerifyClaim(sample, exp);
                        if (claimProblems.Count > 0) { problems.AddRange(claimProblems); continue; }
                        parentSamples.Add(new ServingParentSample(
                            sample.Operation, sample.Sequence, sample.Stratum, sample.WallSeconds,
                            ServingParentClassifier.PointStatus(sample.CorrectnessStatus, sample.WallSeconds),
                            sample.CorrectnessStatus, sample.ActualCardinality, sample.ActualDigest, sample.Error));
                    }
                }
            }
        }
        else
        {
            problems.Add($"process outcome {process.Outcome} is not a trustworthy completed result");
            // Forensic sample capture for any stable ended process (crash,
            // protocol error, or timeout with observed wrapper exit).
            if (process.WrapperExitObserved && File.Exists(samplePhysical) && !Directory.Exists(samplePhysical))
            {
                try { session.RegisterExisting(sampleRel); }
                catch (Exception ex) { problems.Add($"failed to register forensic sample artifact: {ex.Message}"); captureOk = false; }
            }
        }

        // Resource evidence. Fail closed on the frozen active-file rule: never
        // snapshot/register a resource output while the wrapper may still write it.
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
            operation,
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

        return new ServingChildEvidenceResult
        {
            Operation = operation,
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

    private static ServingChildEvidenceResult FailureResult(string operation, int repetition, TimeSpan watchdog, string reason)
        => new()
        {
            Operation = operation,
            Repetition = repetition,
            ProcessOutcome = ProcessOutcome.ParentError,
            ResourceStatus = ResourceMeasurementStatus.Unavailable,
            ParentSamples = Array.Empty<ServingParentSample>(),
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

    private static ChildRequestEnvelope BuildRequest(RunIdentity identity, string operation, int repetition, string candidatePath, string workloadPath)
        => new()
        {
            ProtocolVersion = identity.ProtocolVersion,
            CandidateId = identity.CandidateId,
            CandidateConfigId = identity.CandidateConfigId,
            WorkloadId = identity.WorkloadId,
            CorpusId = identity.CorpusId,
            WorkloadClass = WorkloadClass.Serving,
            Operation = operation,
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

    private static bool ValidateEnvelopeMapping(ChildResultEnvelope env, List<string> problems)
    {
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
            return false;
        }
        return true;
    }

    private static bool ValidateSequenceExact(string operation, IReadOnlyList<ServingProbe> measured, IReadOnlyList<ServingTimedSample> raw, List<string> problems)
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
        if (raw.Count > 0 && raw.Count < measured.Count && raw[^1].CorrectnessStatus != ServingStatuses.Error)
        {
            problems.Add("incomplete measured sequence is only legitimate as an ERROR prefix");
            ok = false;
        }
        return ok;
    }

    private static bool ValidateEnvelopeForms(
        string operation,
        IReadOnlyList<ServingProbe> measured,
        IReadOnlyList<ServingTimedSample> raw,
        ChildResultEnvelope env,
        List<string> problems)
    {
        bool ok = true;
        string envStatus = env.CorrectnessStatus;
        var statuses = raw.Select(s => s.CorrectnessStatus).ToList();
        bool hasInvalid = statuses.Contains(ServingStatuses.Invalid);
        bool hasMeasuredError = statuses.Contains(ServingStatuses.Error);
        int errorCount = statuses.Count(s => s == ServingStatuses.Error);

        if (envStatus is ServingStatuses.Valid or ServingStatuses.Invalid)
        {
            if (env.ErrorCategory is not null || env.ErrorMessage is not null)
            {
                problems.Add($"{envStatus} envelope must not carry ERROR diagnostics");
                ok = false;
            }
        }

        if (envStatus == ServingStatuses.Valid)
        {
            if (raw.Count != measured.Count) { problems.Add("VALID envelope requires complete measured sequence"); ok = false; }
            if (!statuses.All(s => s == ServingStatuses.Valid))
            {
                problems.Add("VALID envelope requires every sample correctness VALID");
                ok = false;
            }
        }
        else if (envStatus == ServingStatuses.Invalid)
        {
            // The child contract never produces an INVALID envelope that
            // contains a measured ERROR sample.
            if (hasMeasuredError)
            {
                problems.Add("INVALID envelope must never contain measured ERROR samples");
                ok = false;
            }
            else if (raw.Count == 0)
            {
                // warmup INVALID
            }
            else if (raw.Count != measured.Count)
            {
                problems.Add("INVALID envelope with a partial measured sequence is impossible");
                ok = false;
            }
            else
            {
                bool s1TailAllValid = operation == "S1";
                if (!hasInvalid && !s1TailAllValid)
                {
                    problems.Add("complete INVALID envelope without any INVALID sample is impossible (S1 Tail excluded)");
                    ok = false;
                }
            }
        }
        else if (envStatus == ServingStatuses.Error)
        {
            if (string.IsNullOrEmpty(env.ErrorCategory) || string.IsNullOrEmpty(env.ErrorMessage))
            {
                problems.Add("ERROR envelope requires non-empty ErrorCategory and ErrorMessage");
                ok = false;
            }
            if (raw.Count == 0)
            {
                if (env.ErrorCategory is not ("warmup" or "runtime"))
                {
                    problems.Add("ERROR zero-sample envelope requires ErrorCategory warmup|runtime");
                    ok = false;
                }
            }
            else if (env.ErrorCategory == "tail")
            {
                if (operation != "S1" || raw.Count != measured.Count || hasMeasuredError)
                {
                    problems.Add("tail ERROR requires S1, complete measured samples and no measured ERROR");
                    ok = false;
                }
            }
            else if (env.ErrorCategory == "timed-probe")
            {
                // Exactly one measured ERROR, and it must be the final sample.
                if (errorCount != 1 || raw[^1].CorrectnessStatus != ServingStatuses.Error)
                {
                    problems.Add("timed-probe ERROR requires exactly one measured ERROR as the final sample");
                    ok = false;
                }
                else if (raw[^1].Error is null || env.ErrorMessage != raw[^1].Error)
                {
                    problems.Add("ERROR prefix envelope message must match the final ERROR sample");
                    ok = false;
                }
                if (raw.Count < measured.Count && raw[^1].CorrectnessStatus != ServingStatuses.Error)
                {
                    // unreachable given the checks above; defensive
                    problems.Add("incomplete timed sequence must end in ERROR");
                    ok = false;
                }
            }
            else
            {
                problems.Add("ERROR envelope uses an unknown ErrorCategory");
                ok = false;
            }
        }
        return ok;
    }

}

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
    public bool RegisteredStableArtifacts { get; init; }
}

/// <summary>
/// One-child serving orchestrator. Writes request evidence into a caller-owned
/// EvidenceStagingSession, launches one resource-wrapped child, strictly parses
/// and independently validates the raw sample artifact, derives parent timed
/// statuses and writes deterministic execution evidence. Never finalizes or
/// promotes; never runs the 15-child loop.
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

        // Preflight: child-produced sample path must be absent.
        string requestPhysical = physical(requestRel);
        string samplePhysical = physical(sampleRel);
        if (File.Exists(samplePhysical) || Directory.Exists(samplePhysical))
            return FailureResult(operation, repetition, watchdog, $"child sample path already exists: {sampleRel}");
        if (File.Exists(requestPhysical))
            return FailureResult(operation, repetition, watchdog, $"request path already exists: {requestRel}");
        string resourcePhysical = physical(resourceRel);
        if (File.Exists(resourcePhysical))
            return FailureResult(operation, repetition, watchdog, $"resource path already exists: {resourceRel}");

        var envelope = BuildRequest(session.Identity, operation, repetition, candidatePath, workloadPath);
        try
        {
            session.WriteText(requestRel, ProtocolJson.ToJson(envelope));
        }
        catch (Exception ex)
        {
            return FailureResult(operation, repetition, watchdog, $"failed to write request evidence: {ex.Message}");
        }

        ProcessInvocation inner = childInvocationFactory(requestPhysical);
        ResourceMeasuredProcessResult resource;
        try
        {
            resource = await resourceRunner(inner, resourcePhysical, watchdog, envelope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return FailureResult(operation, repetition, watchdog, $"resource-wrapped child execution failed: {ex.Message}");
        }

        var process = resource.ProcessResult;
        bool trusted = process.Outcome == ProcessOutcome.CompletedProtocolResult && !process.TimedOut;
        var problems = new List<string>();
        var parentSamples = new List<ServingParentSample>();
        bool complete = false;

        // stdout/stderr evidence (always owned by parent when produced).
        WriteOwned(session, stdoutRel, process.Stdout ?? "", problems);
        WriteOwned(session, stderrRel, process.Stderr ?? "", problems);

        if (trusted)
        {
            var env = process.ParsedChildResult;
            if (env is null)
            {
                problems.Add("completed child produced no parsed envelope");
            }
            else
            {
                sampleProducer?.Invoke(samplePhysical);

                var measured = ServingTimingRunner.Select(workload.Probes, operation, measuredOnly: true);
                List<ServingTimedSample> raw = new();
                if (!File.Exists(samplePhysical))
                {
                    problems.Add("missing serving sample artifact after trustworthy exit-0 envelope");
                }
                else
                {
                    try
                    {
                        session.RegisterExisting(sampleRel);
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"failed to register child sample artifact: {ex.Message}");
                    }
                    try
                    {
                        raw.AddRange(ServingSampleParser.Parse(File.ReadAllBytes(samplePhysical)));
                    }
                    catch (ServingSampleParseException ex)
                    {
                        problems.Add($"malformed serving sample artifact: {ex.Message}");
                    }
                }

                if (problems.Count == 0)
                {
                    problems.AddRange(ValidateConsistency(operation, measured, raw, env));
                    if (problems.Count == 0)
                    {
                        foreach (var sample in raw)
                        {
                            var exp = workload.Expected[(sample.Operation, sample.Sequence)];
                            var claimProblems = ServingParentClassifier.VerifyClaim(sample, exp);
                            if (claimProblems.Count > 0)
                            {
                                problems.AddRange(claimProblems);
                                continue;
                            }
                            parentSamples.Add(new ServingParentSample(
                                sample.Operation, sample.Sequence, sample.Stratum, sample.WallSeconds,
                                ServingParentClassifier.PointStatus(sample.CorrectnessStatus, sample.WallSeconds),
                                sample.CorrectnessStatus, sample.ActualCardinality, sample.ActualDigest, sample.Error));
                        }
                    }
                }
                complete = raw.Count == measured.Count;
            }
        }
        else
        {
            problems.Add($"process outcome {process.Outcome} is not a trustworthy completed result");
            if (!process.TimedOut && process.Outcome == ProcessOutcome.ProcessCrashOrNonzeroExit && File.Exists(samplePhysical))
            {
                try { session.RegisterExisting(sampleRel); } catch (Exception ex) { problems.Add($"failed to register forensic sample artifact: {ex.Message}"); }
            }
        }

        // Stable resource raw output retained through the resource classifier.
        if (File.Exists(resourcePhysical))
        {
            bool stable = !process.TimedOut || process.WrapperExitObserved;
            if (stable)
            {
                try { session.RegisterExisting(resourceRel); } catch (Exception ex) { problems.Add($"failed to register resource output: {ex.Message}"); }
            }
        }

        WriteOwned(session, processRel, JsonOf(new
        {
            outcome = process.Outcome.ToString(),
            timedOut = process.TimedOut,
            exitCode = process.ExitCode,
            wrapperExitObserved = process.WrapperExitObserved,
            elapsedParentWallSeconds = process.ElapsedParentWallSeconds,
        }), problems);
        WriteOwned(session, executionRel, JsonOf(new
        {
            operation,
            repetition,
            watchdog_seconds = watchdog.TotalSeconds,
            evidence_valid = problems.Count == 0,
            measured_sequence_complete = complete,
            resource_status = resource.ResourceStatus.ToString(),
            external_peak_rss_bytes = resource.ExternalPeakRssBytes,
            resource_error = resource.ResourceError,
            sample_count = parentSamples.Count,
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
            MeasuredSequenceComplete = complete,
            EvidenceValid = problems.Count == 0,
            EvidenceProblems = problems,
            WatchdogSeconds = watchdog.TotalSeconds,
            RegisteredStableArtifacts = true,
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

    private static void WriteOwned(EvidenceStagingSession session, string rel, string text, List<string> problems)
    {
        try { session.WriteText(rel, text); }
        catch (Exception ex) { problems.Add($"failed to write {rel}: {ex.Message}"); }
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

    /// <summary>Exact-position measured-sequence + envelope consistency validation.</summary>
    private static IReadOnlyList<string> ValidateConsistency(
        string operation,
        IReadOnlyList<ServingProbe> measured,
        IReadOnlyList<ServingTimedSample> raw,
        ChildResultEnvelope env)
    {
        var problems = new List<string>();
        if (env.CorrectnessStatus is not (ServingStatuses.Valid or ServingStatuses.Invalid or ServingStatuses.Error))
            problems.Add($"envelope correctness '{env.CorrectnessStatus}' invalid");

        for (int i = 0; i < raw.Count; i++)
        {
            var s = raw[i];
            if (i >= measured.Count)
            {
                problems.Add($"sample list overlong at position {i}");
                break;
            }
            var m = measured[i];
            if (s.Operation != m.Op || s.Sequence != m.Seq || s.Stratum != m.Stratum)
                problems.Add($"sample at position {i} does not match measured sequence ({s.Operation},{s.Sequence},{s.Stratum}) vs ({m.Op},{m.Seq},{m.Stratum})");
        }
        if (raw.Count > 0 && raw.Count < measured.Count && raw[^1].CorrectnessStatus != ServingStatuses.Error)
            problems.Add("incomplete measured sequence is only legitimate as an ERROR prefix");

        string envStatus = env.CorrectnessStatus;
        var statuses = raw.Select(s => s.CorrectnessStatus).ToList();
        bool allValid = statuses.All(s => s == ServingStatuses.Valid);
        bool hasInvalid = statuses.Contains(ServingStatuses.Invalid);

        if (envStatus == ServingStatuses.Valid)
        {
            if (raw.Count != measured.Count) problems.Add("VALID envelope requires complete measured sequence");
            if (!allValid) problems.Add("VALID envelope requires every sample correctness VALID");
        }
        else if (envStatus == ServingStatuses.Invalid)
        {
            if (raw.Count == measured.Count && allValid)
            {
                // S1 Tail can make the envelope INVALID with all-VALID measured samples.
                if (operation != "S1") problems.Add("INVALID envelope inconsistent with complete all-VALID measured samples for non-S1");
            }
            else if (raw.Count == 0)
            {
                // warmup INVALID
            }
            else if (!hasInvalid)
            {
                problems.Add("INVALID envelope requires a confirmed INVALID sample or zero samples");
            }
        }
        else if (envStatus == ServingStatuses.Error)
        {
            bool zero = raw.Count == 0;
            bool errorEnd = raw.Count > 0 && raw[^1].CorrectnessStatus == ServingStatuses.Error;
            bool tailError = operation == "S1" && raw.Count == measured.Count && allValid && env.ErrorCategory == "tail";
            if (!zero && !errorEnd && !tailError)
                problems.Add("ERROR envelope requires zero samples, an ERROR-ending prefix, or an S1 Tail ERROR");
        }
        return problems;
    }
}

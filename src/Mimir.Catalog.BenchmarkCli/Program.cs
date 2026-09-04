using System.Text.Json;
using Mimir.Catalog.Benchmark;
using Mimir.Catalog.BenchmarkCli.Protocol;
using Mimir.Catalog.Storage.Sqlite;

namespace Mimir.Catalog.BenchmarkCli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return Usage();
        return args[0] switch
        {
            "parent" => RunParent(args),
            "child" => RunChild(args),
            _ => Usage(),
        };
    }

    private static int RunParent(string[] args)
    {
        Console.Error.WriteLine("parent orchestration is not implemented in 4d.1a");
        return ProtocolExitCodes.ParentNotImplemented;
    }

    private static int RunChild(string[] args)
    {
        int i = 1;
        string? requestPath = null;
        while (i < args.Length)
        {
            if (args[i] == "--request" && i + 1 < args.Length)
            {
                requestPath = args[i + 1];
                i += 2;
            }
            else
            {
                Console.Error.WriteLine($"child: unknown argument '{args[i]}'");
                return ProtocolExitCodes.FatalProtocolError;
            }
        }
        if (requestPath is null)
        {
            Console.Error.WriteLine("child: --request <path> is required");
            return ProtocolExitCodes.FatalProtocolError;
        }

        ChildRequestEnvelope request;
        try
        {
            request = ChildRequestValidator.ReadAndValidate(requestPath);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"child: request protocol failure: {ex.Message}");
            return ProtocolExitCodes.FatalProtocolError;
        }
        catch (RequestValidationException ex)
        {
            Console.Error.WriteLine($"child: request rejected: {ex.Message}");
            return ProtocolExitCodes.RequestValidationRejected;
        }

        if (request.WorkloadClass == WorkloadClass.Serving)
            return RunServingChild(request, requestPath);
        if (request.WorkloadClass == WorkloadClass.G1)
            return RunG1Child(request, requestPath);
        if (request.WorkloadClass == WorkloadClass.G2)
            return RunG2Child(request, requestPath);

        // Placeholder: other workload classes are implemented in later sub-slices.
        // No benchmark ERROR result is fabricated; no result document is emitted.
        Console.Error.WriteLine($"child: workload class {request.WorkloadClass} execution is not implemented");
        return ProtocolExitCodes.ExecutionNotImplemented;
    }

    private static int RunServingChild(ChildRequestEnvelope request, string requestPath)
        => RunServingChildCore(
            request,
            requestPath,
            dir => ServingWorkloadLoader.Load(dir),
            () => new SqliteStorageCandidate(request.CandidatePath));

    /// <summary>
    /// Internal composition seam: production uses ServingWorkloadLoader.Load and
    /// SqliteStorageCandidate; tests inject fixture loader/candidate while still
    /// exercising the exact production artifact/envelope code paths.
    /// </summary>
    internal static int RunServingChildCore(
        ChildRequestEnvelope request,
        string requestPath,
        Func<string, ServingWorkload> workloadLoader,
        Func<IStorageCandidate> candidateFactory)
    {
        if (request.Operation is not ("S1" or "S2" or "S3" or "S4" or "S5"))
        {
            Console.Error.WriteLine($"child: serving operation must be S1-S5, got '{request.Operation}'");
            return ProtocolExitCodes.FatalProtocolError;
        }

        // Workload loading validates authoritative benchmark input. A missing,
        // corrupt or wrong-identity package is NOT a benchmark ERROR: no
        // trustworthy envelope, no sample artifact, nonzero exit.
        ServingWorkload workload;
        try
        {
            workload = workloadLoader(request.WorkloadPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"child: workload load failure: {ex.Message}");
            return ProtocolExitCodes.FatalProtocolError;
        }

        string artifactPath = ServingArtifactPath(requestPath);

        // Candidate/runtime failures after a valid workload are a trustworthy
        // serving ERROR with retained diagnostics and a zero-record artifact.
        ServingTimingExecution execution;
        try
        {
            using IStorageCandidate candidate = candidateFactory();
            candidate.Open();
            execution = new ServingTimingRunner(candidate, workload, request.Operation, request.Repetition).Execute();
        }
        catch (Exception ex)
        {
            execution = new ServingTimingExecution
            {
                Operation = request.Operation,
                Repetition = request.Repetition,
                Correctness = ServingStatuses.Error,
                Samples = Array.Empty<ServingTimedSample>(),
                TimedPassWallSeconds = null,
                ErrorCategory = "runtime",
                ErrorMessage = ex.Message,
            };
            Console.Error.WriteLine($"child: serving execution failure: {ex.Message}");
        }

        try
        {
            ServingSampleArtifact.WriteCreateNew(artifactPath, execution.Samples);
        }
        catch (Exception ex)
        {
            // The sample artifact is part of a trustworthy child result; failure
            // to write it means no trustworthy envelope is emitted.
            Console.Error.WriteLine($"child: failed to write serving sample artifact: {ex.Message}");
            return ProtocolExitCodes.FatalProtocolError;
        }

        LogicalStatus status = execution.Correctness switch
        {
            ServingStatuses.Valid => LogicalStatus.Valid,
            ServingStatuses.Invalid => LogicalStatus.Invalid,
            _ => LogicalStatus.Error,
        };
        var result = new ChildResultEnvelope
        {
            ProtocolVersion = request.ProtocolVersion,
            CandidateId = request.CandidateId,
            CandidateConfigId = request.CandidateConfigId,
            WorkloadId = request.WorkloadId,
            CorpusId = request.CorpusId,
            WorkloadClass = request.WorkloadClass,
            Operation = request.Operation,
            Repetition = request.Repetition,
            Status = status,
            CorrectnessStatus = execution.Correctness,
            WallSeconds = execution.TimedPassWallSeconds,
            ResultCardinality = null,
            ResultDigest = null,
            ErrorCategory = status == LogicalStatus.Error ? execution.ErrorCategory : null,
            ErrorMessage = status == LogicalStatus.Error ? execution.ErrorMessage : null,
        };
        ProtocolJson.WriteSingleDocument(Console.Out, result);
        return ProtocolExitCodes.ValidProtocolResult;
    }

    private static int RunG1Child(ChildRequestEnvelope request, string requestPath)
        => RunG1ChildCore(
            request,
            requestPath,
            dir => GraphWorkloadLoader.Load(dir),
            () => new SqliteStorageCandidate(request.CandidatePath));

    /// <summary>
    /// Internal composition seam: production uses GraphWorkloadLoader.Load and
    /// SqliteStorageCandidate; tests inject fixture loader/candidate while still
    /// exercising the exact production artifact/envelope code paths.
    /// </summary>
    internal static int RunG1ChildCore(
        ChildRequestEnvelope request,
        string requestPath,
        Func<string, GraphWorkload> workloadLoader,
        Func<IStorageCandidate> candidateFactory)
    {
        if (request.Operation != "G1")
        {
            Console.Error.WriteLine($"child: G1 operation must be G1, got '{request.Operation}'");
            return ProtocolExitCodes.FatalProtocolError;
        }

        GraphWorkload workload;
        try
        {
            workload = workloadLoader(request.WorkloadPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"child: workload load failure: {ex.Message}");
            return ProtocolExitCodes.FatalProtocolError;
        }

        string artifactPath = G1ArtifactPath(requestPath);

        G1TimingExecution execution;
        try
        {
            using IStorageCandidate candidate = candidateFactory();
            candidate.Open();
            execution = new G1TimingRunner(candidate, workload, request.Repetition).Execute();
        }
        catch (Exception ex)
        {
            execution = new G1TimingExecution
            {
                Repetition = request.Repetition,
                Correctness = ServingStatuses.Error,
                Samples = Array.Empty<G1TimedSample>(),
                TimedPassWallSeconds = null,
                ErrorCategory = "runtime",
                ErrorMessage = ex.Message,
            };
            Console.Error.WriteLine($"child: G1 execution failure: {ex.Message}");
        }

        try
        {
            G1SampleArtifact.WriteCreateNew(artifactPath, execution.Samples);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"child: failed to write G1 sample artifact: {ex.Message}");
            return ProtocolExitCodes.FatalProtocolError;
        }

        LogicalStatus status = execution.Correctness switch
        {
            ServingStatuses.Valid => LogicalStatus.Valid,
            ServingStatuses.Invalid => LogicalStatus.Invalid,
            _ => LogicalStatus.Error,
        };
        var result = new ChildResultEnvelope
        {
            ProtocolVersion = request.ProtocolVersion,
            CandidateId = request.CandidateId,
            CandidateConfigId = request.CandidateConfigId,
            WorkloadId = request.WorkloadId,
            CorpusId = request.CorpusId,
            WorkloadClass = request.WorkloadClass,
            Operation = "G1",
            Repetition = request.Repetition,
            Status = status,
            CorrectnessStatus = execution.Correctness,
            WallSeconds = execution.TimedPassWallSeconds,
            ResultCardinality = null,
            ResultDigest = null,
            ErrorCategory = status == LogicalStatus.Error ? execution.ErrorCategory : null,
            ErrorMessage = status == LogicalStatus.Error ? execution.ErrorMessage : null,
        };
        ProtocolJson.WriteSingleDocument(Console.Out, result);
        return ProtocolExitCodes.ValidProtocolResult;
    }

    private static int RunG2Child(ChildRequestEnvelope request, string requestPath)
        => RunG2ChildCore(
            request,
            requestPath,
            dir => GraphWorkloadLoader.LoadG2(dir),
            () => new SqliteStorageCandidate(request.CandidatePath));

    /// <summary>
    /// Internal composition seam: production uses GraphWorkloadLoader.LoadG2 and
    /// SqliteStorageCandidate; tests inject fixture loader/candidate while still
    /// exercising the exact production artifact/envelope code paths.
    /// </summary>
    internal static int RunG2ChildCore(
        ChildRequestEnvelope request,
        string requestPath,
        Func<string, G2Workload> workloadLoader,
        Func<IStorageCandidate> candidateFactory)
    {
        if (request.Operation != "G2")
        {
            Console.Error.WriteLine($"child: G2 operation must be G2, got '{request.Operation}'");
            return ProtocolExitCodes.FatalProtocolError;
        }

        G2Workload workload;
        try
        {
            workload = workloadLoader(request.WorkloadPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"child: workload load failure: {ex.Message}");
            return ProtocolExitCodes.FatalProtocolError;
        }

        string artifactPath = G2ArtifactPath(requestPath);

        G2TimingExecution execution;
        try
        {
            using IStorageCandidate candidate = candidateFactory();
            candidate.Open();
            execution = new G2TimingRunner(candidate, workload, request.Repetition).Execute();
        }
        catch (Exception ex)
        {
            execution = new G2TimingExecution
            {
                Repetition = request.Repetition,
                Correctness = ServingStatuses.Error,
                PerInputResults = Array.Empty<G2TimedPerInputResult>(),
                BatchResult = null,
                ErrorCategory = "runtime",
                ErrorMessage = ex.Message,
            };
            Console.Error.WriteLine($"child: G2 execution failure: {ex.Message}");
        }

        try
        {
            G2ResultArtifact.WriteCreateNew(artifactPath, execution.PerInputResults, execution.BatchResult);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"child: failed to write G2 result artifact: {ex.Message}");
            return ProtocolExitCodes.FatalProtocolError;
        }

        LogicalStatus status = execution.Correctness switch
        {
            ServingStatuses.Valid => LogicalStatus.Valid,
            ServingStatuses.Invalid => LogicalStatus.Invalid,
            _ => LogicalStatus.Error,
        };
        bool hasBatch = execution.BatchResult is not null;
        var result = new ChildResultEnvelope
        {
            ProtocolVersion = request.ProtocolVersion,
            CandidateId = request.CandidateId,
            CandidateConfigId = request.CandidateConfigId,
            WorkloadId = request.WorkloadId,
            CorpusId = request.CorpusId,
            WorkloadClass = request.WorkloadClass,
            Operation = "G2",
            Repetition = request.Repetition,
            Status = status,
            CorrectnessStatus = execution.Correctness,
            WallSeconds = hasBatch ? execution.BatchResult!.WallSeconds : null,
            ResultCardinality = hasBatch ? execution.BatchResult!.ActualCardinality : null,
            ResultDigest = hasBatch ? execution.BatchResult!.ActualDigest : null,
            ErrorCategory = status == LogicalStatus.Error ? execution.ErrorCategory : null,
            ErrorMessage = status == LogicalStatus.Error ? execution.ErrorMessage : null,
        };
        ProtocolJson.WriteSingleDocument(Console.Out, result);
        return ProtocolExitCodes.ValidProtocolResult;
    }

    internal static string G2ArtifactPath(string requestPath)
    {
        string dir = Path.GetDirectoryName(requestPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(requestPath);
        return Path.Combine(dir, name + ".g2-results.jsonl");
    }

    internal static string G1ArtifactPath(string requestPath)
    {
        string dir = Path.GetDirectoryName(requestPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(requestPath);
        return Path.Combine(dir, name + ".g1-samples.jsonl");
    }

    internal static string ServingArtifactPath(string requestPath)
    {
        string dir = Path.GetDirectoryName(requestPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(requestPath);
        return Path.Combine(dir, name + ".serving-samples.jsonl");
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: mimir-catalog-benchmark <parent|child> ...");
        Console.Error.WriteLine("       mimir-catalog-benchmark child --request <request.json>");
        return ProtocolExitCodes.FatalProtocolError;
    }
}

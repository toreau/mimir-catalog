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

        // Placeholder: other workload classes are implemented in later sub-slices.
        // No benchmark ERROR result is fabricated; no result document is emitted.
        Console.Error.WriteLine($"child: workload class {request.WorkloadClass} execution is not implemented");
        return ProtocolExitCodes.ExecutionNotImplemented;
    }

    private static int RunServingChild(ChildRequestEnvelope request, string requestPath)
    {
        if (request.Operation is not ("S1" or "S2" or "S3" or "S4" or "S5"))
        {
            Console.Error.WriteLine($"child: serving operation must be S1-S5, got '{request.Operation}'");
            return ProtocolExitCodes.FatalProtocolError;
        }

        ServingTimingExecution execution;
        try
        {
            var workload = ServingWorkloadLoader.Load(request.WorkloadPath);
            using var candidate = new SqliteStorageCandidate(request.CandidatePath);
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
            };
            Console.Error.WriteLine($"child: serving execution failure: {ex.Message}");
        }

        string artifactPath = ServingArtifactPath(requestPath);
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
        };
        ProtocolJson.WriteSingleDocument(Console.Out, result);
        return ProtocolExitCodes.ValidProtocolResult;
    }

    private static string ServingArtifactPath(string requestPath)
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

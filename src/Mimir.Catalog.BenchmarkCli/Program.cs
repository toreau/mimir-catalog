using System.Text.Json;
using Mimir.Catalog.BenchmarkCli.Protocol;

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

        // 4d.1a placeholder: workload-class execution is implemented in later sub-slices.
        // No benchmark ERROR result is fabricated; no result document is emitted.
        Console.Error.WriteLine($"child: workload class {request.WorkloadClass} execution is not implemented in 4d.1a");
        return ProtocolExitCodes.ExecutionNotImplemented;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: mimir-catalog-benchmark <parent|child> ...");
        Console.Error.WriteLine("       mimir-catalog-benchmark child --request <request.json>");
        return ProtocolExitCodes.FatalProtocolError;
    }
}

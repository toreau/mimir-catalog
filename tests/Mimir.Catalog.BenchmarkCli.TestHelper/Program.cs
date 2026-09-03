using System.Text;
using System.Text.Json;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.TestHelper;

/// <summary>
/// Deterministic test-only child process. Mode is the first argument; some
/// modes accept --request &lt;path&gt; to mirror the request identity into the
/// emitted result envelope.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return 9;
        string mode = args[0];
        string? requestPath = Arg(args, "--request");

        switch (mode)
        {
            case "valid-result":
                EmitResult(requestPath, LogicalStatus.Valid);
                return 0;
            case "invalid-result":
                EmitResult(requestPath, LogicalStatus.Invalid);
                return 0;
            case "error-result":
                EmitResult(requestPath, LogicalStatus.Error);
                return 0;
            case "nonzero-with-valid-json":
                EmitResult(requestPath, LogicalStatus.Valid);
                return 3;
            case "stderr-with-valid-result":
                Console.Error.WriteLine("helper diagnostic line");
                EmitResult(requestPath, LogicalStatus.Valid);
                return 0;
            case "malformed-stdout":
                Console.Out.Write("{\"protocolVersion\":");
                return 0;
            case "multiple-json":
                EmitResult(requestPath, LogicalStatus.Valid);
                Console.Out.WriteLine("{\"other\":true}");
                return 0;
            case "missing-stdout":
                Console.Error.WriteLine("helper produced no stdout result");
                return 0;
            case "mismatch-result":
                EmitResult(requestPath, LogicalStatus.Valid, tamper: true);
                return 0;
            case "large-output":
            {
                int n = ArgInt(args, "--bytes", 3_000_000);
                Console.Out.Write(new string('x', n));
                Console.Out.WriteLine();
                Console.Error.Write(new string('y', n));
                Console.Error.WriteLine();
                return 0;
            }
            case "delay":
            {
                int ms = ArgInt(args, "--ms", 5_000);
                Thread.Sleep(ms);
                return 0;
            }
            default:
                return 9;
        }
    }

    private static void EmitResult(string? requestPath, LogicalStatus status, bool tamper = false)
    {
        ChildRequestEnvelope request;
        if (requestPath is not null)
        {
            byte[] bytes = File.ReadAllBytes(requestPath);
            request = ProtocolJson.DeserializeStrict<ChildRequestEnvelope>(bytes);
        }
        else
        {
            request = new ChildRequestEnvelope
            {
                ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
                CandidateId = CandidateAIdentity.CandidateId,
                CandidateConfigId = CandidateAIdentity.CandidateConfigId,
                WorkloadId = CandidateAIdentity.WorkloadId,
                CorpusId = CandidateAIdentity.CorpusId,
                WorkloadClass = WorkloadClass.Serving,
                Operation = "S1",
                Repetition = 1,
                CandidatePath = "/none",
                WorkloadPath = "/none",
                RunId = "test",
            };
        }

        var result = new ChildResultEnvelope
        {
            ProtocolVersion = tamper ? "wrong" : request.ProtocolVersion,
            CandidateId = request.CandidateId,
            CandidateConfigId = request.CandidateConfigId,
            WorkloadId = request.WorkloadId,
            CorpusId = request.CorpusId,
            WorkloadClass = request.WorkloadClass,
            Operation = request.Operation,
            Repetition = request.Repetition,
            Status = status,
            CorrectnessStatus = status.ToString(),
            WallSeconds = 1.25,
        };
        ProtocolJson.WriteSingleDocument(Console.Out, result);
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }

    private static int ArgInt(string[] args, string name, int fallback)
        => int.TryParse(Arg(args, name), out int v) ? v : fallback;
}

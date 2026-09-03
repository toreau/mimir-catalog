using System.Text;
using System.Text.Json;
using Mimir.Catalog.BenchmarkCli;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class ProtocolTests
{
    private static ChildRequestEnvelope ValidRequest() => new()
    {
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        WorkloadClass = WorkloadClass.Analytical,
        Operation = "A1-Concept",
        Repetition = 1,
        CandidatePath = "/data/candidates/sqlite-native-v1.db",
        WorkloadPath = "/data/benchmarks/w",
        RunId = "run-abc",
    };

    private static ChildResultEnvelope Result(LogicalStatus s) => new()
    {
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        WorkloadClass = WorkloadClass.Analytical,
        Operation = "A1-Concept",
        Repetition = 1,
        Status = s,
        CorrectnessStatus = s.ToString(),
    };

    [Fact]
    public void Request_RoundTrip_PreservesFields()
    {
        var json = ProtocolJson.ToJson(ValidRequest());
        var back = ProtocolJson.DeserializeStrict<ChildRequestEnvelope>(Encoding.UTF8.GetBytes(json));
        Assert.Equal(ProtocolConstants.ChildProtocolVersion, back.ProtocolVersion);
        Assert.Equal(CandidateAIdentity.CandidateConfigId, back.CandidateConfigId);
        Assert.Equal(WorkloadClass.Analytical, back.WorkloadClass);
        Assert.Equal("A1-Concept", back.Operation);
        Assert.Equal(1, back.Repetition);
        Assert.Equal("/data/candidates/sqlite-native-v1.db", back.CandidatePath);
    }

    [Theory]
    [InlineData("Valid")]
    [InlineData("Invalid")]
    [InlineData("Error")]
    public void Result_Status_SerializesExactly(string status)
    {
        var s = Enum.Parse<LogicalStatus>(status);
        var r = Result(s);
        string json = ProtocolJson.ToJson(r);
        Assert.Contains($"\"status\":\"{status}\"", json);
        var back = ProtocolJson.DeserializeStrict<ChildResultEnvelope>(Encoding.UTF8.GetBytes(json));
        Assert.Equal(s, back.Status);
    }

    [Fact]
    public void ProtocolModel_NoTimeoutStatus()
    {
        // Authoritative TIMEOUT belongs to the parent; the child logical model must not claim it.
        Assert.Equal(new[] { "Valid", "Invalid", "Error" }, Enum.GetNames<LogicalStatus>());
    }

    [Fact]
    public void Validate_AcceptsExactVersionAndIdentity()
    {
        Assert.Empty(ChildRequestValidator.Validate(ValidRequest()));
    }

    [Fact]
    public void UnknownVersion_Rejected()
    {
        var r = ValidRequest();
        r.ProtocolVersion = "mimir-catalog-benchmark-child-v2";
        Assert.Contains(ChildRequestValidator.Validate(r), e => e.Contains("unsupported protocol version"));
    }

    [Theory]
    [InlineData("candidateId")]
    [InlineData("candidateConfigId")]
    [InlineData("workloadId")]
    [InlineData("corpusId")]
    public void IdentityMismatch_Rejected(string field)
    {
        var r = ValidRequest();
        switch (field)
        {
            case "candidateId": r.CandidateId = "other"; break;
            case "candidateConfigId": r.CandidateConfigId = "other"; break;
            case "workloadId": r.WorkloadId = "other"; break;
            default: r.CorpusId = "other"; break;
        }
        Assert.Contains(ChildRequestValidator.Validate(r), e => e.Contains(field == "candidateId" ? "candidate id mismatch" : field == "candidateConfigId" ? "candidate config id mismatch" : field == "workloadId" ? "workload id mismatch" : "corpus id mismatch"));
    }

    [Fact]
    public void RuntimePaths_DoNotAffectIdentityValidation()
    {
        var r = ValidRequest();
        r.CandidatePath = "/arbitrary/runtime/path-a.db";
        r.WorkloadPath = "/arbitrary/runtime/path-b";
        r.RunId = "correlation-only";
        Assert.Empty(ChildRequestValidator.Validate(r));
    }

    [Fact]
    public void RepetitionZero_Rejected()
    {
        var r = ValidRequest();
        r.Repetition = 0;
        Assert.Contains(ChildRequestValidator.Validate(r), e => e.Contains("repetition must be positive"));
    }

    [Fact]
    public void MalformedJson_Rejected()
    {
        Assert.ThrowsAny<JsonException>(() =>
            ProtocolJson.DeserializeStrict<ChildRequestEnvelope>(Encoding.UTF8.GetBytes("{\"protocolVersion\":")));
    }

    [Fact]
    public void TrailingGarbage_Rejected()
    {
        string json = ProtocolJson.ToJson(ValidRequest()) + " extra";
        Assert.ThrowsAny<JsonException>(() =>
            ProtocolJson.DeserializeStrict<ChildRequestEnvelope>(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void MissingRequiredField_Rejected()
    {
        var r = ValidRequest();
        string json = JsonSerializer.Serialize(new { protocolVersion = r.ProtocolVersion, candidateId = r.CandidateId });
        Assert.ThrowsAny<JsonException>(() =>
            ProtocolJson.DeserializeStrict<ChildRequestEnvelope>(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void UnknownWorkloadClass_Rejected()
    {
        Assert.ThrowsAny<JsonException>(() =>
            ProtocolJson.DeserializeStrict<ChildRequestEnvelope>(Encoding.UTF8.GetBytes(
                ProtocolJson.ToJson(ValidRequest()).Replace("\"Analytical\"", "\"teleport\""))));
    }

    [Fact]
    public void Writer_EmitsExactlyOneDocument()
    {
        using var sw = new StringWriter();
        ProtocolJson.WriteSingleDocument(sw, Result(LogicalStatus.Valid));
        string text = sw.ToString();
        Assert.Equal(1, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("Result:", text);
        _ = JsonDocument.Parse(text); // parses cleanly as one document
    }

    [Fact]
    public void ProtocolModels_HaveNoSqliteTypes()
    {
        var ns = typeof(ChildRequestEnvelope).Namespace!;
        var bad = typeof(ChildRequestEnvelope).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetProperties())
            .Where(p => p.PropertyType.Namespace?.StartsWith("Mimir.Catalog.Storage.Sqlite", StringComparison.Ordinal) == true)
            .ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void ProjectDependencies_CompositionRootOnly()
    {
        string cli = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Mimir.Catalog.BenchmarkCli/Mimir.Catalog.BenchmarkCli.csproj"));
        Assert.Contains("Mimir.Catalog.Storage.Sqlite", cli);
        Assert.Contains("Mimir.Catalog.Benchmark", cli);

        string bm = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Mimir.Catalog.Benchmark/Mimir.Catalog.Benchmark.csproj"));
        Assert.DoesNotContain("Mimir.Catalog.Storage.Sqlite", bm);
    }
}

public class ChildProcessContractTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "mimir-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Write(string dir, string name, string content)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static ChildRequestEnvelope Request() => new()
    {
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = CandidateAIdentity.CandidateId,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        WorkloadClass = WorkloadClass.Serving,
        Operation = "S1",
        Repetition = 1,
        CandidatePath = "/data/candidate.db",
        WorkloadPath = "/data/workload",
        RunId = "run-x",
    };

    private static (int Exit, string Stdout, string Stderr) RunChild(string requestPath)
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        try
        {
            Console.SetOut(outW);
            Console.SetError(errW);
            int exit = Program.Main(new[] { "child", "--request", requestPath });
            return (exit, outW.ToString(), errW.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void MalformedRequest_NoResultNonzeroExit()
    {
        string dir = TempDir();
        string req = Write(dir, "bad.json", "{\"protocolVersion\":");
        var (exit, stdout, stderr) = RunChild(req);
        Assert.Equal(ProtocolExitCodes.FatalProtocolError, exit);
        Assert.Empty(stdout);
        Assert.Contains("protocol failure", stderr);
    }

    [Fact]
    public void IdentityMismatch_RejectedExit2_NoStdout()
    {
        string dir = TempDir();
        var r = Request();
        r.CandidateConfigId = "other";
        string req = Write(dir, "id.json", ProtocolJson.ToJson(r));
        var (exit, stdout, stderr) = RunChild(req);
        Assert.Equal(ProtocolExitCodes.RequestValidationRejected, exit);
        Assert.Empty(stdout);
        Assert.Contains("candidate config id mismatch", stderr);
    }

    [Fact]
    public void UnknownWorkloadClass_RejectedExit1_NoStdout()
    {
        string dir = TempDir();
        string json = ProtocolJson.ToJson(Request()).Replace("\"Serving\"", "\"teleport\"");
        string req = Write(dir, "class.json", json);
        var (exit, stdout, _) = RunChild(req);
        Assert.Equal(ProtocolExitCodes.FatalProtocolError, exit);
        Assert.Empty(stdout);
    }

    [Fact]
    public void ValidRequest_PlaceholderExit3_NoFabricatedResult()
    {
        string dir = TempDir();
        string req = Write(dir, "valid.json", ProtocolJson.ToJson(Request()));
        var (exit, stdout, stderr) = RunChild(req);
        Assert.Equal(ProtocolExitCodes.ExecutionNotImplemented, exit);
        Assert.Empty(stdout); // no benchmark result is fabricated in 4d.1a
        Assert.Contains("not implemented in 4d.1a", stderr);
    }

    [Fact]
    public void ParentMode_NotImplementedDiagnostic()
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        try
        {
            Console.SetOut(outW);
            Console.SetError(errW);
            int exit = Program.Main(new[] { "parent" });
            Assert.Equal(ProtocolExitCodes.ParentNotImplemented, exit);
            Assert.Empty(outW.ToString());
            Assert.Contains("not implemented in 4d.1a", errW.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }
}

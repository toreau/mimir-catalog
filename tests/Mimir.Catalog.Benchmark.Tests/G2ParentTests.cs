using System.Text;
using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class G2ResultParserTests
{
    private const string PerInput =
        "{\"kind\":\"per-input\",\"operation\":\"G2\",\"sequence\":500,\"item\":0,\"qid\":1000," +
        "\"source_stratum\":\"P31Degree1\",\"correctness_status\":\"VALID\",\"actual_cardinality\":1,\"actual_digest\":\"abc\"}";

    private const string Batch =
        "{\"kind\":\"batch\",\"operation\":\"G2\",\"sequence\":500,\"wall_seconds\":1.5," +
        "\"correctness_status\":\"VALID\",\"actual_cardinality\":2,\"actual_digest\":\"bat\"}";

    private static byte[] Artifact(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    [Fact]
    public void Parse_Complete_Valid_Empty()
    {
        var doc = G2ResultParser.Parse(Artifact(PerInput, Batch));
        Assert.Single(doc.PerInput);
        Assert.Equal(0, doc.PerInput[0].Item);
        Assert.Equal(1000, doc.PerInput[0].Qid);
        Assert.NotNull(doc.Batch);
        Assert.Equal(1.5, doc.Batch!.WallSeconds);
        var zero = G2ResultParser.Parse(Array.Empty<byte>());
        Assert.Empty(zero.PerInput);
        Assert.Null(zero.Batch);
    }

    [Fact]
    public void Parse_Ordering_MultipleBatch_AfterBatch_Rejected()
    {
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(Batch, Batch)));
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(Batch, PerInput)));
    }

    [Theory]
    [InlineData("\uFEFF" + PerInput + "\n", "BOM")]
    [InlineData(PerInput + "\r\n", "CRLF")]
    [InlineData(PerInput, "no final LF")]
    [InlineData(PerInput + "\n\n", "blank record")]
    public void Parse_EncodingViolations_Rejected(string content, string _)
    {
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void Parse_InvalidUtf8_Rejected()
    {
        byte[] invalid = { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0xFF, 0x7D };
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(invalid));
    }

    [Fact]
    public void Parse_InvalidKind_Rejected()
    {
        var bad = PerInput.Replace("\"kind\":\"per-input\"", "\"kind\":\"other\"", StringComparison.Ordinal);
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(bad)));
    }

    [Fact]
    public void Parse_ExplicitNullOptional_Rejected()
    {
        string withNull = PerInput.Replace(",\"actual_digest\":\"abc\"", ",\"actual_digest\":null", StringComparison.Ordinal);
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(withNull)));
        string batchNull = Batch.Replace(",\"actual_digest\":\"bat\"", ",\"error\":null", StringComparison.Ordinal);
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(batchNull)));
    }

    [Fact]
    public void Parse_WrongFieldsForKind_Rejected()
    {
        string perWithWall = PerInput.Replace(",\"actual_cardinality\":1", ",\"wall_seconds\":1,\"actual_cardinality\":1", StringComparison.Ordinal);
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(perWithWall)));
        string batchWithItem = Batch.Replace(",\"actual_cardinality\":2", ",\"item\":0,\"actual_cardinality\":2", StringComparison.Ordinal);
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(batchWithItem)));
    }

    [Fact]
    public void Parse_BadWallAndDuplicateAndUnknown_Rejected()
    {
        string neg = Batch.Replace("\"wall_seconds\":1.5", "\"wall_seconds\":-1", StringComparison.Ordinal);
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(neg)));
        string dup = PerInput.Replace("\"item\":0", "\"item\":0,\"item\":1", StringComparison.Ordinal);
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(dup)));
        string unknown = PerInput.Replace("\"correctness_status\":\"VALID\"", "\"bogus\":1,\"correctness_status\":\"VALID\"", StringComparison.Ordinal);
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(unknown)));
        string badStatus = PerInput.Replace("\"correctness_status\":\"VALID\"", "\"correctness_status\":\"TIMEOUT\"", StringComparison.Ordinal);
        Assert.ThrowsAny<G2ResultParseException>(() => G2ResultParser.Parse(Artifact(badStatus)));
    }
}

public class G2ParentClassifierTests
{
    private static G2PerInputExpected Per(int item = 0, long qid = 1000, string stratum = "P31Degree1", long card = 1, string digest = "abc")
        => new(item, qid, stratum, card, digest);

    private static G2RawPerInput RawValid(long? card = 1, string? digest = "abc")
        => new(0, 1000, "P31Degree1", "VALID", card, digest);

    private static G2RawPerInput RawInvalid(long? card = 1, string? digest = "abc")
        => new(0, 1000, "P31Degree1", "INVALID", card, digest);

    [Theory]
    [InlineData(119.999, TimedResultStatus.Valid)]
    [InlineData(120.0, TimedResultStatus.Timeout)]
    [InlineData(121.0, TimedResultStatus.Timeout)]
    public void BatchWall_Boundary(double wall, TimedResultStatus expected)
        => Assert.Equal(expected, G2ParentClassifier.PointStatus("VALID", wall));

    [Theory]
    [InlineData("INVALID", 121.0, TimedResultStatus.Invalid)]
    [InlineData("ERROR", 121.0, TimedResultStatus.Error)]
    public void CorrectnessOutranksLatency(string correctness, double wall, TimedResultStatus expected)
        => Assert.Equal(expected, G2ParentClassifier.PointStatus(correctness, wall));

    [Fact]
    public void PerInputValid_Claims()
    {
        Assert.Empty(G2ParentClassifier.VerifyPerInputClaim(RawValid(), Per()));
        Assert.Contains(G2ParentClassifier.VerifyPerInputClaim(RawValid(digest: "zzz"), Per()), p => p.Contains("digest mismatch"));
        Assert.NotEmpty(G2ParentClassifier.VerifyPerInputClaim(new G2RawPerInput(0, 1000, "P31Degree1", "VALID"), Per()));
        var withError = RawValid() with { Error = "x" };
        Assert.Contains(G2ParentClassifier.VerifyPerInputClaim(withError, Per()), p => p.Contains("carries an error"));
    }

    [Fact]
    public void PerInputInvalid_MustDiffer()
    {
        Assert.Empty(G2ParentClassifier.VerifyPerInputClaim(RawInvalid(card: 9), Per()));
        Assert.Contains(G2ParentClassifier.VerifyPerInputClaim(RawInvalid(card: 1, digest: "abc"), Per()), p => p.Contains("equals expected"));
    }

    [Fact]
    public void PerInputError_RequiresMessage_NoActuals()
    {
        var raw = new G2RawPerInput(0, 1000, "P31Degree1", "ERROR", Error: "boom");
        Assert.Empty(G2ParentClassifier.VerifyPerInputClaim(raw, Per()));
        Assert.Contains(G2ParentClassifier.VerifyPerInputClaim(raw with { Error = null }, Per()), p => p.Contains("without an error message"));
        Assert.Contains(G2ParentClassifier.VerifyPerInputClaim(raw with { ActualCardinality = 1 }, Per()), p => p.Contains("actual facts"));
    }

    private static G2RawBatch RawBatch(string status = "VALID", long? card = 2, string? digest = "bat", string? error = null)
        => new(1.5, status, card, digest, error);

    [Fact]
    public void BatchValid_RequiresAllPerInputValid()
    {
        var batch = RawBatch();
        Assert.Empty(G2ParentClassifier.VerifyBatchClaim(batch, new G2BatchExpected(2, "bat"), new[] { "VALID", "VALID" }));
        Assert.Contains(G2ParentClassifier.VerifyBatchClaim(batch, new G2BatchExpected(2, "bat"), new[] { "VALID", "INVALID" }), p => p.Contains("not independently VALID"));
    }

    [Fact]
    public void BatchInvalid_DemonstratedByPerInputOrActuals()
    {
        var viaPerInput = G2ParentClassifier.VerifyBatchClaim(RawBatch("INVALID"), new G2BatchExpected(2, "bat"), new[] { "VALID", "INVALID" });
        Assert.Empty(viaPerInput);
        var viaActual = G2ParentClassifier.VerifyBatchClaim(RawBatch("INVALID", card: 9), new G2BatchExpected(2, "bat"), new[] { "VALID", "VALID" });
        Assert.Empty(viaActual);
        Assert.Contains(G2ParentClassifier.VerifyBatchClaim(RawBatch("INVALID"), new G2BatchExpected(2, "bat"), new[] { "VALID", "VALID" }), p => p.Contains("without a demonstrated mismatch"));
        Assert.Contains(G2ParentClassifier.VerifyBatchClaim(RawBatch("INVALID"), new G2BatchExpected(2, "bat"), new[] { "VALID", "ERROR" }), p => p.Contains("ERROR"));
    }

    [Fact]
    public void BatchError_RequiresPerInputError_NoActuals()
    {
        var raw = RawBatch("ERROR", error: "boom", card: null, digest: null);
        Assert.Empty(G2ParentClassifier.VerifyBatchClaim(raw, new G2BatchExpected(2, "bat"), new[] { "VALID", "ERROR" }));
        Assert.Contains(G2ParentClassifier.VerifyBatchClaim(raw, new G2BatchExpected(2, "bat"), new[] { "VALID", "VALID" }), p => p.Contains("without an independently ERROR per-input"));
        Assert.Contains(G2ParentClassifier.VerifyBatchClaim(raw with { ActualCardinality = 1 }, new G2BatchExpected(2, "bat"), new[] { "VALID", "ERROR" }), p => p.Contains("actual facts"));
    }
}

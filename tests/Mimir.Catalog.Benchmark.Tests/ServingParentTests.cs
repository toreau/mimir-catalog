using System.Text;
using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class ServingParentTests
{
    [Theory]
    [InlineData(4.999, TimedResultStatus.Valid)]
    [InlineData(5.0, TimedResultStatus.Timeout)]
    [InlineData(6.0, TimedResultStatus.Timeout)]
    public void ValidChildWall_Boundary(double wall, TimedResultStatus expected)
        => Assert.Equal(expected, ServingParentClassifier.PointStatus("VALID", wall));

    [Theory]
    [InlineData(6.0, "INVALID", TimedResultStatus.Invalid)]
    [InlineData(6.0, "ERROR", TimedResultStatus.Error)]
    [InlineData(4.0, "INVALID", TimedResultStatus.Invalid)]
    public void CorrectnessFailure_OutranksLatency(double wall, string correctness, TimedResultStatus expected)
        => Assert.Equal(expected, ServingParentClassifier.PointStatus(correctness, wall));

    [Fact]
    public void VerifyClaim_Valid_RequiresMatchingActuals()
    {
        var sample = new ServingTimedSample("S1", 1, "Hit", 0.1, "VALID", 1, "abc");
        var expected = new ServingExpected("S1", 1, true, 1, "abc");
        Assert.Empty(ServingParentClassifier.VerifyClaim(sample, expected));

        var wrong = sample with { ActualDigest = "def" };
        Assert.Contains(ServingParentClassifier.VerifyClaim(wrong, expected), p => p.Contains("digest mismatch"));

        var missing = sample with { ActualDigest = null };
        Assert.NotEmpty(ServingParentClassifier.VerifyClaim(missing, expected));
    }

    [Fact]
    public void VerifyClaim_InvalidEqualToExpected_IntegrityProblem()
    {
        var sample = new ServingTimedSample("S1", 1, "Hit", 0.1, "INVALID", 1, "abc");
        var expected = new ServingExpected("S1", 1, true, 1, "abc");
        Assert.Contains(ServingParentClassifier.VerifyClaim(sample, expected), p => p.Contains("equals expected"));
    }

    [Fact]
    public void VerifyClaim_Error_RequiresMessage()
    {
        var ok = new ServingTimedSample("S1", 1, "Hit", 0.1, "ERROR", Error: "boom");
        Assert.Empty(ServingParentClassifier.VerifyClaim(ok, new ServingExpected("S1", 1, true, 0, "x")));
        var bare = ok with { Error = null };
        Assert.NotEmpty(ServingParentClassifier.VerifyClaim(bare, new ServingExpected("S1", 1, true, 0, "x")));
    }
}

public class ServingSampleParserTests
{
    private static string Line(object sample)
        => System.Text.Json.JsonSerializer.Serialize(sample, sample.GetType());

    private static object Good(string status = "VALID") => new
    {
        operation = "S1",
        sequence = 1L,
        stratum = "Hit",
        wall_seconds = 0.5,
        correctness_status = status,
        actual_cardinality = 1L,
        actual_digest = "abc",
        error = (string?)null,
    };

    [Fact]
    public void Parse_Valid_AndEmpty()
    {
        var samples = ServingSampleParser.Parse(Encoding.UTF8.GetBytes(Line(Good()) + "\n" + Line(Good("INVALID")) + "\n"));
        Assert.Equal(2, samples.Count);
        Assert.Equal("VALID", samples[0].CorrectnessStatus);
        Assert.Empty(ServingSampleParser.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void Parse_RejectsBom_BlankMiddle_Unknown_Duplicate_Malformed()
    {
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(
            Encoding.UTF8.GetBytes("\uFEFF" + Line(Good()) + "\n")));
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(
            Encoding.UTF8.GetBytes(Line(Good()) + "\n\n" + Line(Good()) + "\n")));
        var withUnknown = Line(Good()).Replace("correctness_status", "bogus\":1,\"correctness_status", StringComparison.Ordinal);
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(Encoding.UTF8.GetBytes(withUnknown + "\n")));
        var dup = Line(Good()).Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":2", StringComparison.Ordinal);
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(Encoding.UTF8.GetBytes(dup + "\n")));
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(Encoding.UTF8.GetBytes("{\"broken\"")));
    }

    [Fact]
    public void Parse_RejectsChildTimeout_AndInvalidWalls()
    {
        var timeout = Line(Good("TIMEOUT"));
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(Encoding.UTF8.GetBytes(timeout + "\n")));
        var neg = Line(Good()).Replace("wall_seconds\":0.5", "wall_seconds\":-1");
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(Encoding.UTF8.GetBytes(neg + "\n")));
    }

    [Theory]
    [InlineData("{\"operation\":\"S1\",\"sequence\":1,\"stratum\":\"Hit\",\"wall_seconds\":0.5,\"correctness_status\":\"VALID\",\"actual_cardinality\":1,\"actual_digest\":\"abc\"}")] // no final LF
    public void Parse_WriterConventionViolations_Rejected(string line)
    {
        Assert.ThrowsAny<ServingSampleParseException>(() =>
            ServingSampleParser.Parse(Encoding.UTF8.GetBytes(line)));
    }

    [Fact]
    public void Parse_MissingRequired_Rejected()
    {
        const string good = "{\"operation\":\"S1\",\"sequence\":1,\"stratum\":\"Hit\",\"wall_seconds\":0.5,\"correctness_status\":\"VALID\",\"actual_cardinality\":1,\"actual_digest\":\"abc\"}";
        var parsed = ServingSampleParser.Parse(Encoding.UTF8.GetBytes(good + "\n"));
        Assert.Single(parsed);
        string noWall = good.Replace(",\"wall_seconds\":0.5", "", StringComparison.Ordinal);
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(Encoding.UTF8.GetBytes(noWall + "\n")));
        string noSeq = good.Replace(",\"sequence\":1", "", StringComparison.Ordinal);
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(Encoding.UTF8.GetBytes(noSeq + "\n")));
        string noOp = good.Replace("\"operation\":\"S1\",", "", StringComparison.Ordinal);
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(Encoding.UTF8.GetBytes(noOp + "\n")));
    }

    [Fact]
    public void Parse_InvalidUtf8_AndCrlf_Rejected()
    {
        byte[] invalid = { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0xFF, 0x7D }; // raw 0xFF in JSON
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(invalid));
        string crlf = Line(Good()).Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.ThrowsAny<ServingSampleParseException>(() => ServingSampleParser.Parse(Encoding.UTF8.GetBytes(crlf)));
    }
}

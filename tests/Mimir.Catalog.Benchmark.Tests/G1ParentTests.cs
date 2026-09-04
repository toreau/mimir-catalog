using System.Text;
using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class G1SampleParserTests
{
    private const string Good =
        "{\"operation\":\"G1\",\"sequence\":0,\"stratum\":\"Degree1\",\"wall_seconds\":0.5," +
        "\"correctness_status\":\"VALID\",\"actual_cardinality\":0,\"actual_visited\":1,\"actual_digest\":\"abc\"}";

    [Fact]
    public void Parse_Valid_And_Empty()
    {
        var samples = G1SampleParser.Parse(Encoding.UTF8.GetBytes(Good + "\n" + Good.Replace("\"sequence\":0", "\"sequence\":1", StringComparison.Ordinal) + "\n"));
        Assert.Equal(2, samples.Count);
        Assert.Equal("G1", samples[0].Operation);
        Assert.Equal(0, samples[0].ActualCardinality);
        Assert.Equal(1, samples[0].ActualVisited);
        Assert.Empty(G1SampleParser.Parse(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("\uFEFF" + Good + "\n", "BOM")]
    [InlineData(Good + "\r\n", "CRLF")]
    [InlineData(Good, "no final LF")]
    [InlineData(Good + "\n\n" + Good + "\n", "blank record")]
    public void Parse_EncodingViolations_Rejected(string content, string _)
    {
        Assert.ThrowsAny<G1SampleParseException>(() => G1SampleParser.Parse(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void Parse_InvalidUtf8_Rejected()
    {
        byte[] invalid = { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0xFF, 0x7D };
        Assert.ThrowsAny<G1SampleParseException>(() => G1SampleParser.Parse(invalid));
    }

    [Fact]
    public void Parse_FieldViolations_Rejected()
    {
        Assert.ThrowsAny<G1SampleParseException>(() => G1SampleParser.Parse(Encoding.UTF8.GetBytes(
            Good.Replace("\"correctness_status\":\"VALID\"", "bogus\":1,\"correctness_status\":\"VALID\"", StringComparison.Ordinal) + "\n")));
        Assert.ThrowsAny<G1SampleParseException>(() => G1SampleParser.Parse(Encoding.UTF8.GetBytes(
            Good.Replace("\"sequence\":0", "\"sequence\":0,\"sequence\":1", StringComparison.Ordinal) + "\n")));
        Assert.ThrowsAny<G1SampleParseException>(() => G1SampleParser.Parse(Encoding.UTF8.GetBytes(
            Good.Replace("\"wall_seconds\":0.5", "\"wall_seconds\":-1", StringComparison.Ordinal) + "\n")));
        Assert.ThrowsAny<G1SampleParseException>(() => G1SampleParser.Parse(Encoding.UTF8.GetBytes(
            Good.Replace(",\"wall_seconds\":0.5", "", StringComparison.Ordinal) + "\n")));
        Assert.ThrowsAny<G1SampleParseException>(() => G1SampleParser.Parse(Encoding.UTF8.GetBytes(
            Good.Replace("\"correctness_status\":\"VALID\"", "\"correctness_status\":\"TIMEOUT\"", StringComparison.Ordinal) + "\n")));
    }

    [Fact]
    public void Parse_MissingRequired_Rejected()
    {
        string noSeq = Good.Replace(",\"sequence\":0", "", StringComparison.Ordinal);
        Assert.ThrowsAny<G1SampleParseException>(() => G1SampleParser.Parse(Encoding.UTF8.GetBytes(noSeq + "\n")));
        string noOp = Good.Replace("\"operation\":\"G1\",", "", StringComparison.Ordinal);
        Assert.ThrowsAny<G1SampleParseException>(() => G1SampleParser.Parse(Encoding.UTF8.GetBytes(noOp + "\n")));
    }
}

public class G1ParentClassifierTests
{
    private static GraphExpected Expected() => new("G1", 0, true, 0, 1, "abc");

    [Theory]
    [InlineData(29.999, TimedResultStatus.Valid)]
    [InlineData(30.0, TimedResultStatus.Timeout)]
    [InlineData(31.0, TimedResultStatus.Timeout)]
    public void ValidChildWall_Boundary(double wall, TimedResultStatus expected)
        => Assert.Equal(expected, G1ParentClassifier.PointStatus("VALID", wall));

    [Theory]
    [InlineData("INVALID", 31.0, TimedResultStatus.Invalid)]
    [InlineData("ERROR", 31.0, TimedResultStatus.Error)]
    [InlineData("INVALID", 10.0, TimedResultStatus.Invalid)]
    public void CorrectnessOutranksLatency(string correctness, double wall, TimedResultStatus expected)
        => Assert.Equal(expected, G1ParentClassifier.PointStatus(correctness, wall));

    [Fact]
    public void VerifyClaim_Valid()
    {
        var good = new G1TimedSample("G1", 0, "Degree1", 0.5, "VALID", 0, 1, "abc");
        Assert.Empty(G1ParentClassifier.VerifyClaim(good, Expected()));
        var wrongDigest = good with { ActualDigest = "zzz" };
        Assert.Contains(G1ParentClassifier.VerifyClaim(wrongDigest, Expected()), p => p.Contains("digest mismatch"));
        var missingVisited = good with { ActualVisited = null };
        Assert.NotEmpty(G1ParentClassifier.VerifyClaim(missingVisited, Expected()));
        var withError = good with { Error = "boom" };
        Assert.Contains(G1ParentClassifier.VerifyClaim(withError, Expected()), p => p.Contains("carries an error"));
    }

    [Fact]
    public void VerifyClaim_Invalid_MustDiffer()
    {
        var differs = new G1TimedSample("G1", 0, "Degree1", 0.5, "INVALID", 1, 1, "abc");
        Assert.Empty(G1ParentClassifier.VerifyClaim(differs, Expected()));
        var equals = new G1TimedSample("G1", 0, "Degree1", 0.5, "INVALID", 0, 1, "abc");
        Assert.Contains(G1ParentClassifier.VerifyClaim(equals, Expected()), p => p.Contains("equals expected"));
    }

    [Fact]
    public void VerifyClaim_Error_RequiresMessage_NoActuals()
    {
        var ok = new G1TimedSample("G1", 0, "Degree1", 0.5, "ERROR", Error: "boom");
        Assert.Empty(G1ParentClassifier.VerifyClaim(ok, Expected()));
        var bare = ok with { Error = null };
        Assert.Contains(G1ParentClassifier.VerifyClaim(bare, Expected()), p => p.Contains("without an error message"));
        var withActual = ok with { ActualCardinality = 5 };
        Assert.Contains(G1ParentClassifier.VerifyClaim(withActual, Expected()), p => p.Contains("carries actual facts"));
    }
}

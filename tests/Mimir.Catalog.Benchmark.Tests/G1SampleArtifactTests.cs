using System.Text;
using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class G1SampleArtifactTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-g1-art-" + Guid.NewGuid().ToString("N"));

    public G1SampleArtifactTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string PathOf() => Path.Combine(_dir, "req.g1-samples.jsonl");

    [Fact]
    public void WritesDeterministicJsonl_NoBom_OrderedFields_Lf()
    {
        string path = PathOf();
        var samples = new List<G1TimedSample>
        {
            new("G1", 0, "Degree1", 0.5, ServingStatuses.Valid, 1, 2, "dig-abc"),
            new("G1", 1, "Degree2Plus", 1.25, ServingStatuses.Error, Error: "boom"),
        };
        G1SampleArtifact.WriteCreateNew(path, samples);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] != 0xEF && bytes[1] != 0xBB && bytes[2] != 0xBF);
        Assert.Equal(0x0A, bytes[^1]);
        string text = Encoding.UTF8.GetString(bytes);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("{\"operation\":\"G1\",\"sequence\":0,\"stratum\":\"Degree1\",\"wall_seconds\":0.5,\"correctness_status\":\"VALID\",\"actual_cardinality\":1,\"actual_visited\":2,\"actual_digest\":\"dig-abc\"}", lines[0]);
        Assert.Contains("\"correctness_status\":\"ERROR\"", lines[1]);
        Assert.Contains("\"error\":\"boom\"", lines[1]);
        Assert.DoesNotContain("actual_visited", lines[1]); // omitted on error
    }

    [Fact]
    public void ZeroSamples_EmptyValidFile()
    {
        string path = PathOf();
        G1SampleArtifact.WriteCreateNew(path, Array.Empty<G1TimedSample>());
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void CreateNew_CollisionThrows_PreservesExisting()
    {
        string path = PathOf();
        File.WriteAllText(path, "keep");
        Assert.ThrowsAny<IOException>(() => G1SampleArtifact.WriteCreateNew(path, new[] { new G1TimedSample("G1", 0, "Degree1", 1, ServingStatuses.Valid) }));
        Assert.Equal("keep", File.ReadAllText(path));
    }
}

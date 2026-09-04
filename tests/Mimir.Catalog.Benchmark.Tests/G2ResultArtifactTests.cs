using System.Text;
using Mimir.Catalog.Benchmark;

namespace Mimir.Catalog.Benchmark.Tests;

public class G2ResultArtifactTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mimir-g2-art-" + Guid.NewGuid().ToString("N"));

    public G2ResultArtifactTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string PathOf() => Path.Combine(_dir, "req.g2-results.jsonl");

    [Fact]
    public void WritesPerInputThenBatch_DeterministicOrder_Lf_NoBom()
    {
        string path = PathOf();
        var perInput = new List<G2TimedPerInputResult>
        {
            new(0, 1000, "P31Degree1", ServingStatuses.Valid, 1, "dig"),
            new(1, 1001, "P31Degree2Plus", ServingStatuses.Error, Error: "boom"),
        };
        var batch = new G2TimedBatchResult(1.5, ServingStatuses.Valid, 2, "batchdig");
        G2ResultArtifact.WriteCreateNew(path, perInput, batch);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] != 0xEF && bytes[1] != 0xBB && bytes[2] != 0xBF);
        Assert.Equal(0x0A, bytes[^1]);
        var lines = Encoding.UTF8.GetString(bytes).TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("{\"kind\":\"per-input\",\"operation\":\"G2\",\"sequence\":500,\"item\":0,\"qid\":1000,\"source_stratum\":\"P31Degree1\",\"correctness_status\":\"VALID\",\"actual_cardinality\":1,\"actual_digest\":\"dig\"}", lines[0]);
        Assert.Contains("\"kind\":\"per-input\"", lines[1]);
        Assert.Contains("\"error\":\"boom\"", lines[1]);
        Assert.StartsWith("{\"kind\":\"batch\",\"operation\":\"G2\",\"sequence\":500,\"wall_seconds\":1.5,\"correctness_status\":\"VALID\",\"actual_cardinality\":2,\"actual_digest\":\"batchdig\"}", lines[2]);
    }

    [Fact]
    public void BatchError_OmitsNullFacts()
    {
        string path = PathOf();
        var batch = new G2TimedBatchResult(2.0, ServingStatuses.Error, Error: "some failed");
        G2ResultArtifact.WriteCreateNew(path, Array.Empty<G2TimedPerInputResult>(), batch);
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.StartsWith("{\"kind\":\"batch\",\"operation\":\"G2\",\"sequence\":500,\"wall_seconds\":2,\"correctness_status\":\"ERROR\"", text);
        Assert.Contains("\"error\":\"some failed\"", text);
        Assert.DoesNotContain("actual_cardinality", text);
        Assert.DoesNotContain("actual_digest", text);
    }

    [Fact]
    public void ZeroRecords_EmptyValidFile()
    {
        string path = PathOf();
        G2ResultArtifact.WriteCreateNew(path, Array.Empty<G2TimedPerInputResult>(), batch: null);
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void CreateNew_CollisionThrows_PreservesExisting()
    {
        string path = PathOf();
        File.WriteAllText(path, "keep");
        Assert.ThrowsAny<IOException>(() => G2ResultArtifact.WriteCreateNew(path,
            new[] { new G2TimedPerInputResult(0, 1000, "P31Degree1", ServingStatuses.Valid, 1, "dig") }, batch: null));
        Assert.Equal("keep", File.ReadAllText(path));
    }
}

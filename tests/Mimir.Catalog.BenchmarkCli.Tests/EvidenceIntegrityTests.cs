using System.Text;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class EvidenceIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-evi-" + Guid.NewGuid().ToString("N"));

    public EvidenceIntegrityTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private RunIdentity Identity(string runId = "run-int", string schema = EvidenceSchema.Version) => new()
    {
        EvidenceSchemaVersion = schema,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = "sqlite-native-v1",
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = runId,
    };

    [Fact]
    public void PreExistingUnregisteredFile_SurvivesFailedWrite()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        byte[] original = Encoding.UTF8.GetBytes("original-bytes");
        string raw = Path.Combine(s.StagingPath, "raw.txt");
        File.WriteAllBytes(raw, original);

        Assert.Throws<EvidenceStagingException>(() => s.WriteBytes("raw.txt", Encoding.UTF8.GetBytes("replacement")));
        Assert.Equal(original, File.ReadAllBytes(raw));
        Assert.DoesNotContain(s.RegisteredArtifacts, e => e.RelativePath == "raw.txt");
    }

    [Fact]
    public void ParentSymlink_WriteNewRejected_BeforeExternalBytesWritten()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        byte[] victim = Encoding.UTF8.GetBytes("keep-me");
        File.WriteAllBytes(Path.Combine(outside, "victim.txt"), victim);

        string sub = Path.Combine(s.StagingPath, "sub");
        Directory.CreateDirectory(sub);
        Directory.Delete(sub);
        try
        {
            Directory.CreateSymbolicLink(sub, outside);
        }
        catch (Exception)
        {
            return; // platform without symlink support
        }

        Assert.Throws<EvidenceStagingException>(() =>
            s.WriteBytes("sub/new-write.txt", Encoding.UTF8.GetBytes("tampered")));
        Assert.Equal(victim, File.ReadAllBytes(Path.Combine(outside, "victim.txt")));
        Assert.False(File.Exists(Path.Combine(outside, "new-write.txt"))); // no external file written through the link
    }

    [Fact]
    public void ParentSymlink_RegisterExistingRejected()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        string outside = Path.Combine(_root, "outside-reg");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "f.txt"), "x");
        string sub = Path.Combine(s.StagingPath, "sub");
        try
        {
            Directory.CreateSymbolicLink(sub, outside);
        }
        catch (Exception)
        {
            return;
        }
        Assert.Throws<EvidenceStagingException>(() => s.RegisterExisting("sub/f.txt"));
    }

    [Fact]
    public void VerifyDetects_ParentChainReplacedBySymlink()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        byte[] content = Encoding.UTF8.GetBytes("same-content");
        s.WriteBytes("chain/leaf.txt", content);

        string outside = Path.Combine(_root, "outside-verify", "chain");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "leaf.txt"), content); // identical content

        Directory.Delete(Path.Combine(s.StagingPath, "chain"), recursive: true);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(s.StagingPath, "chain"), outside);
        }
        catch (Exception)
        {
            return;
        }

        var problems = s.VerifyRegisteredArtifacts();
        Assert.Contains(problems, p => p.StartsWith("chain/leaf.txt:", StringComparison.Ordinal) && p.Contains("symlink", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SchemaVersionMismatch_Rejected()
    {
        var ex = Assert.Throws<EvidenceStagingException>(() =>
            EvidenceStagingSession.Create(_root, Identity(schema: "not-v1")));
        Assert.Contains("schema version mismatch", ex.Message);
    }

    [Fact]
    public void SessionIdentity_IsStableAndImmutable()
    {
        var id = Identity();
        using var s = EvidenceStagingSession.Create(_root, id);
        Assert.Same(id, s.Identity);
        Assert.Equal(EvidenceSchema.Version, s.Identity.EvidenceSchemaVersion);
        Assert.Equal("sqlite-native-v1", s.Identity.CandidateId);
        Assert.Equal("run-int", s.Identity.RunId);
        // Layout was derived from the immutable identity; it stays stable.
        Assert.Equal(s.Layout.StagingPath, Path.Combine(_root, "sqlite-native-v1", "run-int.staging"));
    }

    [Fact]
    public void RegularFileCollision_AtFinalPath_RejectedAndPreserved()
    {
        string final = Path.Combine(_root, "sqlite-native-v1", "run-file");
        Directory.CreateDirectory(Path.Combine(_root, "sqlite-native-v1"));
        File.WriteAllText(final, "occupied-final");
        var ex = Assert.Throws<EvidenceStagingException>(() => EvidenceStagingSession.Create(_root, Identity("run-file")));
        Assert.Contains("final path already exists", ex.Message);
        Assert.Equal("occupied-final", File.ReadAllText(final));
    }

    [Fact]
    public void RegularFileCollision_AtStagingPath_RejectedAndPreserved()
    {
        string staging = Path.Combine(_root, "sqlite-native-v1", "run-file.staging");
        Directory.CreateDirectory(Path.Combine(_root, "sqlite-native-v1"));
        File.WriteAllText(staging, "occupied-staging");
        var ex = Assert.Throws<EvidenceStagingException>(() => EvidenceStagingSession.Create(_root, Identity("run-file")));
        Assert.Contains("staging path already exists", ex.Message);
        Assert.Equal("occupied-staging", File.ReadAllText(staging));
    }
}

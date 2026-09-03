using System.Security.Cryptography;
using System.Text;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class EvidenceStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-ev-" + Guid.NewGuid().ToString("N"));

    public EvidenceStagingTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private RunIdentity Identity(string candidate = "sqlite-native-v1", string runId = "run-2026-09-03T10.00.00Z") => new()
    {
        EvidenceSchemaVersion = EvidenceSchema.Version,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = candidate,
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = runId,
    };

    private static string Sha(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(bytes));
    }

    [Fact]
    public void SchemaVersion_Frozen()
    {
        Assert.Equal("mimir-catalog-benchmark-evidence-v1", EvidenceSchema.Version);
    }

    [Theory]
    [InlineData("sqlite-native-v1")]          // valid Candidate A id
    [InlineData("run-2026-09-03T10.00.00Z")]  // valid timestamp-like runId
    public void ValidComponents_Accepted(string s) => Assert.True(EvidencePathSafety.IsValidComponent(s));

    [Theory]
    [InlineData(".hidden")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData("/abs")]
    public void InvalidComponents_Rejected(string s) => Assert.False(EvidencePathSafety.IsValidComponent(s));

    [Fact]
    public void Layout_Deterministic_SameParent()
    {
        var layout = RunLayoutPaths.Create(_root, "sqlite-native-v1", "run-1");
        Assert.Equal(Path.Combine(_root, "sqlite-native-v1", "run-1"), layout.FinalPath);
        Assert.Equal(Path.Combine(_root, "sqlite-native-v1", "run-1.staging"), layout.StagingPath);
        Assert.StartsWith(Path.Combine(layout.CandidateRoot, "run-1"), layout.FinalPath);
        Assert.Equal(layout.FinalPath + ".staging", layout.StagingPath);
    }

    [Fact]
    public void InvalidIds_Throw()
    {
        Assert.Throws<EvidenceStagingException>(() => RunLayoutPaths.Create(_root, "..", "r"));
        Assert.Throws<EvidenceStagingException>(() => RunLayoutPaths.Create(_root, "c", ".."));
        Assert.Throws<EvidenceStagingException>(() => RunLayoutPaths.Create(_root, "c", "r/s"));
        Assert.Throws<EvidenceStagingException>(() => RunLayoutPaths.Create(_root, "c", ""));
        Assert.Throws<EvidenceStagingException>(() => RunLayoutPaths.Create("", "c", "r"));
    }

    [Fact]
    public void SessionCreation_WritesRunning_NoComplete()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        Assert.True(Directory.Exists(s.StagingPath));
        Assert.False(Directory.Exists(s.FinalPath));
        var state = ReadState(s.StagingPath);
        Assert.Equal("Running", state.State);
        Assert.Equal("run-2026-09-03T10.00.00Z", state.RunId);
        Assert.Equal("sqlite-native-v1", state.CandidateId);
        Assert.DoesNotContain("Complete", File.ReadAllText(Path.Combine(s.StagingPath, "run.state.json")));
    }

    [Fact]
    public void Collision_ExistingStagingOrFinal_Rejected_AndPreserved()
    {
        var id = Identity(runId: "collide");
        using (EvidenceStagingSession.Create(_root, id)) { }
        Assert.Throws<EvidenceStagingException>(() => EvidenceStagingSession.Create(_root, id));
        Assert.True(Directory.Exists(Path.Combine(_root, "sqlite-native-v1", "collide.staging")));

        string final = Path.Combine(_root, "sqlite-native-v1", "final-x");
        Directory.CreateDirectory(final);
        Assert.Throws<EvidenceStagingException>(() => EvidenceStagingSession.Create(_root, Identity(runId: "final-x")));
        Assert.True(Directory.Exists(final));
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("serving/S1/request.json", true)]
    [InlineData("a/../b", false)]
    [InlineData("../x", false)]
    [InlineData("/abs", false)]
    [InlineData("trail/", false)]
    [InlineData("a\\b", false)]
    [InlineData("x//y", false)]
    [InlineData("x/./y", false)]
    [InlineData("a:", false)]
    public void ArtifactPathValidation(string rel, bool valid)
    {
        Assert.Equal(valid, EvidencePathSafety.TryValidateArtifactPath(rel, out _));
    }

    [Theory]
    [InlineData("run.state.json")]
    [InlineData("run.json")]
    [InlineData("evidence.manifest.json")]
    public void ReservedControlPaths_Rejected(string rel)
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        Assert.True(EvidenceStagingSession.IsReservedControlPath(rel));
        Assert.Throws<EvidenceStagingException>(() => s.WriteBytes(rel, new byte[] { 1 }));
        Assert.Throws<EvidenceStagingException>(() => s.RegisterExisting(rel));
    }

    [Fact]
    public void WriteBytes_CreateNew_ShaAndLength()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        byte[] payload = Encoding.UTF8.GetBytes("abc");
        var entry = s.WriteBytes("a.txt", payload);
        Assert.Equal("a.txt", entry.RelativePath);
        Assert.Equal(3L, entry.Bytes);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", entry.Sha256);
        Assert.Equal(Sha(payload), entry.Sha256);
    }

    [Fact]
    public void WriteText_DeterministicUtf8_NoBom()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        string text = "åøæ — text";
        s.WriteText("utf8.txt", text);
        var bytes = File.ReadAllBytes(Path.Combine(s.StagingPath, "utf8.txt"));
        Assert.Equal(Encoding.UTF8.GetBytes(text), bytes); // no BOM prepended
    }

    [Fact]
    public void NestedArtifact_CreatesDirectories_CanonicalRetained()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteBytes("serving/S1/request.json", new byte[] { 1, 2 });
        Assert.True(File.Exists(Path.Combine(s.StagingPath, "serving", "S1", "request.json")));
        Assert.Contains(s.RegisteredArtifacts, e => e.RelativePath == "serving/S1/request.json");
    }

    [Fact]
    public void OverwriteAndDuplicate_Rejected()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteBytes("f.txt", new byte[] { 1 });
        Assert.Throws<EvidenceStagingException>(() => s.WriteBytes("f.txt", new byte[] { 2 }));
        // Pre-existing but unregistered file is still not overwritten.
        File.WriteAllText(Path.Combine(s.StagingPath, "raw.txt"), "x");
        Assert.Throws<EvidenceStagingException>(() => s.WriteBytes("raw.txt", new byte[] { 3 }));
    }

    [Fact]
    public void RegisterExisting_RecordsExactFacts_NoCopy()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        string rel = "resource/time-output.txt";
        string full = Path.Combine(s.StagingPath, "resource");
        Directory.CreateDirectory(full);
        string file = Path.Combine(full, "time-output.txt");
        byte[] content = Encoding.UTF8.GetBytes("12345  maximum resident set size\n");
        File.WriteAllBytes(file, content);
        var entry = s.RegisterExisting(rel);
        Assert.Equal(rel, entry.RelativePath);
        Assert.Equal(content.Length, (int)entry.Bytes);
        Assert.Equal(Sha(content), entry.Sha256);
        Assert.Equal(content, File.ReadAllBytes(file)); // unmodified, not copied elsewhere
        Assert.Throws<EvidenceStagingException>(() => s.RegisterExisting(rel));
    }

    [Fact]
    public void Inventory_EnumeratesOrdinalByRelativePath()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteBytes("z.txt", new byte[] { 1 });
        s.WriteBytes("a/b.txt", new byte[] { 2 });
        s.WriteBytes("A.txt", new byte[] { 3 });
        var paths = s.RegisteredArtifacts.Select(e => e.RelativePath).ToList();
        Assert.Equal(paths.OrderBy(x => x, StringComparer.Ordinal), paths);
    }

    [Fact]
    public void VerifyDetects_Mutation_Deletion_DirectoryReplacement()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteBytes("a.txt", Encoding.UTF8.GetBytes("hello"));
        s.WriteBytes("b.txt", Encoding.UTF8.GetBytes("bye"));
        Assert.Empty(s.VerifyRegisteredArtifacts());

        File.WriteAllText(Path.Combine(s.StagingPath, "a.txt"), "HELLO"); // same byte length as "hello"
        Assert.Contains(s.VerifyRegisteredArtifacts(), p => p == "a.txt: content changed");

        File.Delete(Path.Combine(s.StagingPath, "b.txt"));
        Assert.Contains(s.VerifyRegisteredArtifacts(), p => p == "b.txt: missing");

        s.WriteBytes("c.txt", new byte[] { 9 });
        File.Delete(Path.Combine(s.StagingPath, "c.txt"));
        Directory.CreateDirectory(Path.Combine(s.StagingPath, "c.txt"));
        Assert.Contains(s.VerifyRegisteredArtifacts(), p => p.StartsWith("c.txt:"));
    }

    [Fact]
    public void SymlinkArtifact_Rejected_WhereSupported()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        string outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "x");
        string link = Path.Combine(s.StagingPath, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception)
        {
            return; // platform without symlink support: skip capability test only
        }
        Assert.Throws<EvidenceStagingException>(() => s.RegisterExisting("link.txt"));
    }

    [Fact]
    public void UnexpectedFiles_EnumerationDistinguishes()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteBytes("registered.json", new byte[] { 1 });
        File.WriteAllText(Path.Combine(s.StagingPath, "stray.bin"), "x");
        Directory.CreateDirectory(Path.Combine(s.StagingPath, "serving", "S1"));
        File.WriteAllText(Path.Combine(s.StagingPath, "serving", "S1", "request.json"), "x");
        var unexpected = s.FindUnexpectedFiles();
        Assert.Contains("stray.bin", unexpected);
        Assert.Contains("serving/S1/request.json", unexpected);
        Assert.DoesNotContain("registered.json", unexpected);
        Assert.DoesNotContain(EvidenceStagingSession.StateFileName, unexpected);
    }

    [Fact]
    public void Fail_WritesFailedState_RetainsEverything_NoFinal_NoComplete()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteBytes("d.json", Encoding.UTF8.GetBytes("{\"k\":1}"));
        string artifactPath = Path.Combine(s.StagingPath, "d.json");
        var warnings = s.Fail("analytical", "boom during run");
        Assert.Empty(warnings);
        Assert.Equal("Failed", ReadState(s.StagingPath).State);
        Assert.True(File.Exists(artifactPath));
        Assert.True(Directory.Exists(s.StagingPath));
        Assert.False(Directory.Exists(s.FinalPath));
        string stateText = File.ReadAllText(Path.Combine(s.StagingPath, "run.state.json"));
        Assert.DoesNotContain("Complete", stateText);
    }

    private static RunEvidenceState ReadState(string staging)
        => System.Text.Json.JsonSerializer.Deserialize<RunEvidenceState>(
            File.ReadAllText(Path.Combine(staging, "run.state.json")),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}

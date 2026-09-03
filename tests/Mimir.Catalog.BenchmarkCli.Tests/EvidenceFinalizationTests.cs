using System.Text;
using System.Text.Json;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class EvidenceJsonTests
{
    private static RunIdentity Id(string run = "run-json-1") => new()
    {
        EvidenceSchemaVersion = EvidenceSchema.Version,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = "sqlite-native-v1",
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = run,
    };

    [Fact]
    public void RunJson_DeterministicNoBomTrailingLf_SnakeCaseOrder()
    {
        var run = new EvidenceRunJson(EvidenceSchema.Version, ProtocolConstants.ChildProtocolVersion,
            "sqlite-native-v1", CandidateAIdentity.CandidateConfigId, CandidateAIdentity.WorkloadId,
            CandidateAIdentity.CorpusId, "run-x");
        byte[] b1 = EvidenceJson.SerializeRunJson(run);
        byte[] b2 = EvidenceJson.SerializeRunJson(run);
        Assert.Equal(b1, b2);
        Assert.NotEqual(0xEF, b1[0]); // no BOM
        Assert.Equal(0x0A, b1[^1]);   // exactly one trailing LF
        string text = Encoding.UTF8.GetString(b1);
        Assert.Contains("\"evidence_schema_version\":", text);
        Assert.Contains("\"protocol_version\":", text);
        Assert.Contains("\"run_id\":\"run-x\"", text);
        int iSchema = text.IndexOf("evidence_schema_version", StringComparison.Ordinal);
        int iProto = text.IndexOf("protocol_version", StringComparison.Ordinal);
        int iRun = text.IndexOf("run_id", StringComparison.Ordinal);
        Assert.True(iSchema < iProto && iProto < iRun);

        var back = EvidenceJson.ReadRunJson(b1);
        Assert.Equal("run-x", back.RunId);
        Assert.Equal(CandidateAIdentity.CandidateConfigId, back.CandidateConfigId);
    }

    [Fact]
    public void RunJson_UnknownProperty_Rejected()
    {
        var run = RunJsonJsonWithReplacement("");
        Assert.ThrowsAny<JsonException>(() => EvidenceJson.ReadRunJson(Encoding.UTF8.GetBytes(
            run.Replace("\"run_id\":", "\"bogus\":\"x\",\"run_id\":", StringComparison.Ordinal))));
    }

    [Fact]
    public void RunJson_DuplicateProperty_Rejected()
    {
        string json = RunJsonJsonWithReplacement("");
        string dup = json.Replace("\"candidate_id\":\"sqlite-native-v1\",",
            "\"candidate_id\":\"sqlite-native-v1\",\"candidate_id\":\"sqlite-native-v1\",", StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => EvidenceJson.ReadRunJson(Encoding.UTF8.GetBytes(dup)));
    }

    [Fact]
    public void RunJson_MissingProperty_Rejected()
    {
        string json = RunJsonJsonWithReplacement("");
        string missing = json.Replace(",\"run_id\":\"run-json-1\"", "", StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => EvidenceJson.ReadRunJson(Encoding.UTF8.GetBytes(missing)));
    }

    [Fact]
    public void RunJson_WrongType_Rejected()
    {
        string json = RunJsonJsonWithReplacement("");
        string wrong = json.Replace("\"run_id\":\"run-json-1\"", "\"run_id\":5", StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => EvidenceJson.ReadRunJson(Encoding.UTF8.GetBytes(wrong)));
    }

    [Fact]
    public void Manifest_DeterministicOrderMembership_AndShaGrammar()
    {
        var identity = Id();
        var registered = new List<EvidenceArtifactEntry>
        {
            new("serving/S1/result.json", 4, "a".PadRight(64, 'a')),
            new("analytical/A1/result.json", 2, "b".PadRight(64, 'b')),
        };
        var manifest = EvidenceManifestBuilder.Build(identity, registered,
            new ManifestArtifact("run.json", 5, "c".PadRight(64, 'c')));

        byte[] b1 = EvidenceJson.SerializeManifest(manifest);
        byte[] b2 = EvidenceJson.SerializeManifest(manifest);
        Assert.Equal(b1, b2);
        string text = Encoding.UTF8.GetString(b1);
        // ordinal sorting: analytical < run.json < serving
        int iA = text.IndexOf("analytical/A1/result.json", StringComparison.Ordinal);
        int iR = text.IndexOf("\"relative_path\":\"run.json\"", StringComparison.Ordinal);
        int iS = text.IndexOf("serving/S1/result.json", StringComparison.Ordinal);
        Assert.True(iA < iR && iR < iS);
        Assert.DoesNotContain("run.state.json", text);
        Assert.DoesNotContain("evidence.manifest.json", text);

        var parsed = EvidenceJson.ReadManifest(b1);
        Assert.Equal(3, parsed.Artifacts.Count);
        Assert.True(EvidenceJson.IsValidSha256(parsed.Artifacts[0].Sha256));
        Assert.False(EvidenceJson.IsValidSha256("ABC"));
        Assert.False(EvidenceJson.IsValidSha256("ab"));
    }

    private static string RunJsonJsonWithReplacement(string _)
    {
        var id = Id();
        var run = new EvidenceRunJson(id.EvidenceSchemaVersion, id.ProtocolVersion, id.CandidateId,
            id.CandidateConfigId, id.WorkloadId, id.CorpusId, id.RunId);
        return Encoding.UTF8.GetString(EvidenceJson.SerializeRunJson(run));
    }

}

public class EvidenceFinalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-fin-" + Guid.NewGuid().ToString("N"));

    public EvidenceFinalizationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private RunIdentity Identity(string runId = "run-finalize") => new()
    {
        EvidenceSchemaVersion = EvidenceSchema.Version,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = "sqlite-native-v1",
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = runId,
    };

    [Fact]
    public void Success_ReadyForPromotion_RunningState_StagingPresent_FinalAbsent()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("analytical/A1/result.json", "{\"op\":\"A1\"}");
        s.WriteText("top.json", "hello");

        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.ReadyForPromotion, r.Status);
        Assert.Empty(r.Problems);
        Assert.True(Directory.Exists(s.StagingPath));
        Assert.False(Directory.Exists(s.FinalPath));
        Assert.True(File.Exists(Path.Combine(s.StagingPath, "run.json")));
        Assert.True(File.Exists(Path.Combine(s.StagingPath, "evidence.manifest.json")));
        Assert.NotNull(r.ManifestBytes);
        Assert.NotNull(r.ManifestSha256);
        Assert.Equal(64, r.ManifestSha256.Length);
        string stateText = File.ReadAllText(Path.Combine(s.StagingPath, "run.state.json"));
        Assert.Contains("\"state\":\"Running\"", stateText);
        Assert.DoesNotContain("Complete", stateText);
    }

    [Fact]
    public void Success_ManifestOnDiskMatchesDeterministicBytes()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.ReadyForPromotion, r.Status);
        byte[] onDisk = File.ReadAllBytes(Path.Combine(s.StagingPath, "evidence.manifest.json"));
        Assert.Equal(onDisk, r.ManifestBytes);
    }

    [Fact]
    public void UnexpectedFile_Blocks()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        File.WriteAllText(Path.Combine(s.StagingPath, "stray.bin"), "junk");
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("finalize:inventory"));
        Assert.True(File.Exists(Path.Combine(s.StagingPath, "stray.bin")));
        Assert.False(File.Exists(Path.Combine(s.StagingPath, "run.json")));
    }

    [Fact]
    public void LeftoverTempState_Blocks()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        File.WriteAllText(Path.Combine(s.StagingPath, ".state-tmp-abc.json"), "{}");
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
    }

    [Fact]
    public void UnrelatedEmptyDirectory_Blocks()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        Directory.CreateDirectory(Path.Combine(s.StagingPath, "orphan"));
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("finalize:inventory") && p.Contains("orphan"));
    }

    [Fact]
    public void RegisteredMutation_Blocks()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("mut.txt", "hello");
        File.WriteAllText(Path.Combine(s.StagingPath, "mut.txt"), "HELLO"); // same length, content changed
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("finalize:registered"));
        Assert.True(File.Exists(Path.Combine(s.StagingPath, "mut.txt")));
    }

    [Fact]
    public void ExistingFinal_BlocksAndPreserved()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        Directory.CreateDirectory(Path.Combine(_root, "sqlite-native-v1"));
        string final = Path.Combine(_root, "sqlite-native-v1", "run-finalize");
        File.WriteAllText(final, "occupied");
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("final-destination"));
        Assert.Equal("occupied", File.ReadAllText(final));
    }

    [Fact]
    public void StagingRootReplacedBySymlink_Blocks_NoDiagnosticWriteThroughLink()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        string outside = Path.Combine(_root, "outside-root");
        Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "sentinel-unchanged");
        Directory.Delete(s.StagingPath, recursive: true);
        try
        {
            Directory.CreateSymbolicLink(s.StagingPath, outside);
        }
        catch (Exception)
        {
            return;
        }
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("finalize:tree") && p.Contains("symlink"));
        Assert.Contains(r.Problems, p => p.Contains("Failed state not written because staging root is unsafe/unavailable"));
        Assert.Equal("sentinel-unchanged", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(outside, "run.state.json")));
    }

    [Fact]
    public void NestedDirectorySymlink_BlocksBeforeRecursion()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("x/keep.txt", "same-content");
        string outside = Path.Combine(_root, "outside-x", "x");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "same-content");
        Directory.Delete(Path.Combine(s.StagingPath, "x"), recursive: true);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(s.StagingPath, "x"), outside);
        }
        catch (Exception)
        {
            return;
        }
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("finalize:tree") && p.Contains("symlink"));
    }

    [Fact]
    public void SecondFinalize_RefusesExistingRunJson_PreservesIt()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        var r1 = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.ReadyForPromotion, r1.Status);
        byte[] runBefore = File.ReadAllBytes(Path.Combine(s.StagingPath, "run.json"));
        var r2 = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.Failed, r2.Status);
        Assert.Equal(runBefore, File.ReadAllBytes(Path.Combine(s.StagingPath, "run.json")));
        Assert.True(File.Exists(Path.Combine(s.StagingPath, "evidence.manifest.json")));
    }
    [Fact]
    public void RunJsonMutation_AtFinalCheckpoint_Failed()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        var r = EvidenceFinalizer.FinalizeForTest(s, cp =>
        {
            if (cp == EvidenceFinalizer.EvidenceFinalizeCheckpoint.BeforeFinalControlVerification)
            {
                var id = s.Identity;
                var mutated = new EvidenceRunJson(id.EvidenceSchemaVersion, id.ProtocolVersion,
                    "other-candidate", id.CandidateConfigId, id.WorkloadId, id.CorpusId, id.RunId);
                File.WriteAllBytes(Path.Combine(s.StagingPath, "run.json"), EvidenceJson.SerializeRunJson(mutated));
            }
        });
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.DoesNotContain(r.Problems, p => p == "");
    }

    [Fact]
    public void ManifestMutation_AtFinalCheckpoint_Failed()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        var r = EvidenceFinalizer.FinalizeForTest(s, cp =>
        {
            if (cp == EvidenceFinalizer.EvidenceFinalizeCheckpoint.BeforeFinalControlVerification)
            {
                string mpath = Path.Combine(s.StagingPath, "evidence.manifest.json");
                var parsed = EvidenceJson.ReadManifest(File.ReadAllBytes(mpath));
                var tampered = parsed with { Artifacts = parsed.Artifacts
                    .Select(a => a.RelativePath == "run.json" ? a : a with { Sha256 = a.Sha256 == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                        ? a.Sha256 : "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }).ToList() };
                File.WriteAllBytes(mpath, EvidenceJson.SerializeManifest(tampered));
            }
        });
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
    }

    [Fact]
    public void OperationalFault_HookThrows_ReturnsFailed_StateWrittenWhenRootSafe()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        var r = EvidenceFinalizer.FinalizeForTest(s, cp =>
        {
            if (cp == EvidenceFinalizer.EvidenceFinalizeCheckpoint.BeforeFinalControlVerification)
                throw new IOException("injected operational fault");
        });
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("finalize:internal"));
        Assert.True(Directory.Exists(s.StagingPath));
        Assert.False(Directory.Exists(s.FinalPath));
        Assert.Contains("\"state\":\"Failed\"", File.ReadAllText(Path.Combine(s.StagingPath, "run.state.json")));
    }

    [Fact]
    public void FinalResult_ManifestBytesAndShaMatchFreshDisk()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "payload");
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.ReadyForPromotion, r.Status);
        byte[] onDisk = File.ReadAllBytes(Path.Combine(s.StagingPath, "evidence.manifest.json"));
        Assert.Equal(onDisk, r.ManifestBytes);
        using var sha = System.Security.Cryptography.SHA256.Create();
        Assert.Equal(Convert.ToHexStringLower(sha.ComputeHash(onDisk)), r.ManifestSha256);
    }
    [Fact]
    public void LateUnexpectedFile_AfterCheckpoint_Failed()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        var r = EvidenceFinalizer.FinalizeForTest(s, cp =>
        {
            if (cp == EvidenceFinalizer.EvidenceFinalizeCheckpoint.BeforeFinalControlVerification)
                File.WriteAllText(Path.Combine(s.StagingPath, "late-stray.bin"), "junk");
        });
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("late-stray.bin"));
    }

    [Fact]
    public void LateUnexpectedDirectory_AfterCheckpoint_Failed()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        var r = EvidenceFinalizer.FinalizeForTest(s, cp =>
        {
            if (cp == EvidenceFinalizer.EvidenceFinalizeCheckpoint.BeforeFinalControlVerification)
                Directory.CreateDirectory(Path.Combine(s.StagingPath, "orphan-late"));
        });
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("orphan-late"));
    }

    [Fact]
    public void LateSymlink_AfterCheckpoint_Failed_NoWriteThrough()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        string outsideTarget = Path.Combine(_root, "late-target.txt");
        File.WriteAllText(outsideTarget, "target-unchanged");
        var r = EvidenceFinalizer.FinalizeForTest(s, cp =>
        {
            if (cp == EvidenceFinalizer.EvidenceFinalizeCheckpoint.BeforeFinalControlVerification)
            {
                try
                {
                    File.CreateSymbolicLink(Path.Combine(s.StagingPath, "late-link.txt"), outsideTarget);
                }
                catch (Exception)
                {
                    // platform without symlink support
                }
            }
        });
        if (File.Exists(Path.Combine(s.StagingPath, "late-link.txt")))
        {
            Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
            Assert.Contains(r.Problems, p => p.Contains("finalize:tree"));
            Assert.Equal("target-unchanged", File.ReadAllText(outsideTarget));
        }
    }

    [Fact]
    public void LateStateMutation_ToFailed_Blocks()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "x");
        var r = EvidenceFinalizer.FinalizeForTest(s, cp =>
        {
            if (cp == EvidenceFinalizer.EvidenceFinalizeCheckpoint.BeforeFinalControlVerification)
            {
                string mutated = "{\"state\":\"Failed\",\"run_id\":\"" + s.Identity.RunId +
                    "\",\"candidate_id\":\"" + s.Identity.CandidateId + "\"}\n";
                File.WriteAllText(Path.Combine(s.StagingPath, "run.state.json"), mutated);
            }
        });
        Assert.Equal(EvidenceFinalizationStatus.Failed, r.Status);
        Assert.Contains(r.Problems, p => p.Contains("finalize:state") && p.Contains("Running"));
    }

    [Fact]
    public void Success_FinalGate_RunningState_ManifestMatchesFreshDisk()
    {
        using var s = EvidenceStagingSession.Create(_root, Identity());
        s.WriteText("a.json", "payload");
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.ReadyForPromotion, r.Status);
        string stateText = File.ReadAllText(Path.Combine(s.StagingPath, "run.state.json"));
        Assert.Contains("\"state\":\"Running\"", stateText);
        byte[] onDisk = File.ReadAllBytes(Path.Combine(s.StagingPath, "evidence.manifest.json"));
        Assert.Equal(onDisk, r.ManifestBytes);
        using var sha = System.Security.Cryptography.SHA256.Create();
        Assert.Equal(Convert.ToHexStringLower(sha.ComputeHash(onDisk)), r.ManifestSha256);
    }
}

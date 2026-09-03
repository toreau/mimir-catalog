using System.Text;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class PublishedRunValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-pub-" + Guid.NewGuid().ToString("N"));

    public PublishedRunValidatorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Runs => Path.Combine(_root, "runs");

    private RunIdentity Identity(string runId = "run-validate") => new()
    {
        EvidenceSchemaVersion = EvidenceSchema.Version,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = "sqlite-native-v1",
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = runId,
    };

    private RunIdentity Publish(string runId = "run-validate", string payload = "{\"ok\":true}")
    {
        Directory.CreateDirectory(Runs);
        var s = EvidenceStagingSession.Create(Runs, Identity(runId));
        s.WriteText("analytical/A1/result.json", payload);
        var ready = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.ReadyForPromotion, ready.Status);
        var promoted = EvidencePromoter.Promote(s, ready);
        Assert.Equal(EvidencePromotionStatus.Published, promoted.Status);
        return Identity(runId);
    }

    private string Final(RunIdentity id) => Path.Combine(Runs, id.CandidateId, id.RunId);

    [Fact]
    public void HappyPath_Valid_ManifestFactsMatchDisk()
    {
        var id = Publish();
        var r = PublishedRunValidator.Validate(Runs, id);
        Assert.Equal(PublishedRunValidationStatus.Valid, r.Status);
        Assert.Empty(r.Problems);
        Assert.Equal(Final(id), r.FinalPath);
        byte[] disk = File.ReadAllBytes(Path.Combine(Final(id), "evidence.manifest.json"));
        Assert.Equal(disk, r.ManifestBytes);
        Assert.Equal(PublishedRunValidator.Sha256(disk), r.ManifestSha256);
        Assert.NotNull(r.RunJson);
        Assert.NotNull(r.Manifest);
    }

    [Fact]
    public void MissingFinal_Invalid()
    {
        var r = PublishedRunValidator.Validate(Runs, Identity("never-run"));
        Assert.Equal(PublishedRunValidationStatus.Invalid, r.Status);
    }

    [Fact]
    public void OnlyStaging_Invalid()
    {
        var id = Identity();
        Directory.CreateDirectory(Runs);
        var s = EvidenceStagingSession.Create(Runs, id);
        s.WriteText("a.json", "x");
        // no promotion; staging remains
        var r = PublishedRunValidator.Validate(Runs, id);
        Assert.Equal(PublishedRunValidationStatus.Invalid, r.Status);
    }

    [Fact]
    public void ValidFinal_WithSiblingStaging_StillValid()
    {
        var id = Identity("sibling");
        Directory.CreateDirectory(Runs);
        var s = EvidenceStagingSession.Create(Runs, id);
        s.WriteText("a.json", "x");
        var ready = EvidenceFinalizer.Finalize(s);
        var promoted = EvidencePromoter.Promote(s, ready);
        Assert.Equal(EvidencePromotionStatus.Published, promoted.Status);
        // create a fresh sibling staging that is never inspected
        EvidenceStagingSession.Create(Runs, Identity("sibling-2"));
        var r = PublishedRunValidator.Validate(Runs, id);
        Assert.Equal(PublishedRunValidationStatus.Valid, r.Status);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Failed")]
    public void WrongFinalState_Invalid(string state)
    {
        var id = Publish();
        File.WriteAllText(Path.Combine(Final(id), "run.state.json"),
            Encoding.UTF8.GetString(EvidenceState.Serialize(state, id.RunId, id.CandidateId, "create")));
        var r = PublishedRunValidator.Validate(Runs, id);
        Assert.Equal(PublishedRunValidationStatus.Invalid, r.Status);
    }

    [Fact]
    public void CompleteSemanticViolations_Invalid()
    {
        // missing stage
        var id = Publish();
        File.WriteAllText(Path.Combine(Final(id), "run.state.json"),
            Encoding.UTF8.GetString(EvidenceState.Serialize("Complete", id.RunId, id.CandidateId, stage: null, reason: null, utc: DateTime.UtcNow)));
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id).Status);

        // wrong stage
        var id2 = Publish("run-2");
        File.WriteAllText(Path.Combine(Final(id2), "run.state.json"),
            Encoding.UTF8.GetString(EvidenceState.Serialize("Complete", id2.RunId, id2.CandidateId, stage: "bogus", reason: null, utc: DateTime.UtcNow)));
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id2).Status);

        // reason present
        var id3 = Publish("run-3");
        File.WriteAllText(Path.Combine(Final(id3), "run.state.json"),
            Encoding.UTF8.GetString(EvidenceState.Serialize("Complete", id3.RunId, id3.CandidateId, stage: "promote", reason: "why", utc: DateTime.UtcNow)));
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id3).Status);

        // utc missing
        var id4 = Publish("run-4");
        File.WriteAllText(Path.Combine(Final(id4), "run.state.json"),
            Encoding.UTF8.GetString(EvidenceState.Serialize("Complete", id4.RunId, id4.CandidateId, stage: "promote", reason: null, utc: null)));
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id4).Status);
    }

    [Fact]
    public void RunJson_IdentityMutation_Invalid()
    {
        var id = Publish();
        var run = new EvidenceRunJson(id.EvidenceSchemaVersion, id.ProtocolVersion, id.CandidateId,
            id.CandidateConfigId, id.WorkloadId, id.CorpusId, "other-run-id");
        File.WriteAllBytes(Path.Combine(Final(id), "run.json"), EvidenceJson.SerializeRunJson(run));
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id).Status);
    }

    [Fact]
    public void RunJson_ContentMutation_SameLength_Invalid()
    {
        var id = Publish();
        string path = Path.Combine(Final(id), "run.json");
        string original = File.ReadAllText(path);
        // same-length content mutation that also changes identity
        string mutated = original.Replace(id.RunId, "x" + id.RunId[1..], StringComparison.Ordinal);
        Assert.Equal(original.Length, mutated.Length);
        File.WriteAllText(path, mutated);
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id).Status);
    }

    [Fact]
    public void PayloadMutation_AndMissing_Invalid()
    {
        var id = Publish();
        string payload = Path.Combine(Final(id), "analytical", "A1", "result.json");
        File.WriteAllText(payload, "{\"mutated\":true}");
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id).Status);

        var id2 = Publish("run-2");
        File.Delete(Path.Combine(Final(id2), "analytical", "A1", "result.json"));
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id2).Status);
    }

    [Fact]
    public void UnexpectedFileAndEmptyDirectory_Invalid()
    {
        var id = Publish();
        File.WriteAllText(Path.Combine(Final(id), "stray.bin"), "junk");
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id).Status);

        var id2 = Publish("run-2");
        Directory.CreateDirectory(Path.Combine(Final(id2), "orphan"));
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id2).Status);
    }

    [Fact]
    public void Manifest_DuplicateAndUnsorted_Invalid()
    {
        var id = Publish();
        string mp = Path.Combine(Final(id), "evidence.manifest.json");
        var parsed = EvidenceJson.ReadManifest(File.ReadAllBytes(mp));
        var dup = parsed with { Artifacts = parsed.Artifacts.Append(parsed.Artifacts[0]).ToList() };
        File.WriteAllBytes(mp, EvidenceJson.SerializeManifest(dup));
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id).Status);

        var id2 = Publish("run-2");
        string mp2 = Path.Combine(Final(id2), "evidence.manifest.json");
        var parsed2 = EvidenceJson.ReadManifest(File.ReadAllBytes(mp2));
        var unsorted = parsed2 with { Artifacts = parsed2.Artifacts.OrderByDescending(a => a.RelativePath, StringComparer.Ordinal).ToList() };
        File.WriteAllBytes(mp2, EvidenceJson.SerializeManifest(unsorted));
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id2).Status);
    }

    [Fact]
    public void NestedSymlink_Invalid_WhereSupported()
    {
        var id = Publish();
        string outside = Path.Combine(_root, "outside-payload");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(Final(id), "analytical", "A1", "result-link.txt");
        try { File.CreateSymbolicLink(link, Path.Combine(outside, "target.txt")); }
        catch (Exception) { return; }
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id).Status);
    }

    [Fact]
    public void CandidateRootSymlink_Invalid_WhereSupported()
    {
        var id = Publish();
        string candidateRoot = Path.Combine(Runs, id.CandidateId);
        string backup = Path.Combine(_root, "candidate-backup");
        Directory.Move(candidateRoot, backup);
        try { Directory.CreateSymbolicLink(candidateRoot, backup); }
        catch (Exception) { return; }
        Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id).Status);
    }

    [Fact]
    public void InspectionFailure_Error_NotInvalid()
    {
        var id = Publish();
        var r = PublishedRunValidator.ValidateForTest(Runs, id,
            path => EvidencePathSafety.IsSamePath(path, Path.Combine(Runs, id.CandidateId))
                ? NodeKind.InspectionError
                : PublishedRunValidator.InspectNode(path));
        Assert.Equal(PublishedRunValidationStatus.Error, r.Status);
    }

    [Fact]
    public void ReadOnly_InvalidRun_Unchanged()
    {
        var id = Publish();
        string payload = Path.Combine(Final(id), "analytical", "A1", "result.json");
        byte[] before = File.ReadAllBytes(payload);
        string stateBefore = File.ReadAllText(Path.Combine(Final(id), "run.state.json"));
        var r = PublishedRunValidator.Validate(Runs, Identity("run-missing")); // invalid path
        Assert.Equal(PublishedRunValidationStatus.Invalid, r.Status);
        Assert.Equal(before, File.ReadAllBytes(payload));
        Assert.Equal(stateBefore, File.ReadAllText(Path.Combine(Final(id), "run.state.json")));
    }
    [Fact]
    public void ControlLateSymlink_RunJson_Invalid_NotConsumed()
    {
        var id = Publish("ctl-link");
        string runPath = Path.Combine(Final(id), "run.json");
        string outside = Path.Combine(_root, "outside-run.json");
        byte[] original = File.ReadAllBytes(runPath);
        File.WriteAllText(outside, "{\"external\":true}");

        var r = PublishedRunValidator.ValidateForTest(Runs, id, probe: null, cp =>
        {
            if (cp != PublishedValidatorCheckpoint.AfterInitialTreeWalk) return;
            // mutation happens only AFTER the initial walk validated the real file
            File.Delete(runPath);
            try { File.CreateSymbolicLink(runPath, outside); }
            catch (Exception) { File.WriteAllBytes(runPath, original); }
        });
        if (!File.Exists(runPath) || (new FileInfo(runPath).LinkTarget is null && File.ReadAllText(runPath) != "{\"external\":true}"))
            return; // symlink unsupported: fixture restored
        Assert.Equal(PublishedRunValidationStatus.Invalid, r.Status);
        Assert.Equal("{\"external\":true}", File.ReadAllText(outside));
    }

    [Fact]
    public void CandidateRootLateReplacement_Invalid_NoReadThrough()
    {
        var id = Publish("cand-late");
        string candidateRoot = Path.Combine(Runs, id.CandidateId);
        string backup = Path.Combine(_root, "cand-late-backup");
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);

        var r = PublishedRunValidator.ValidateForTest(Runs, id, probe: null, cp =>
        {
            if (cp != PublishedValidatorCheckpoint.AfterInitialTreeWalk) return;
            // mutation only after the initial walk saw an ordinary CandidateRoot
            Directory.Move(candidateRoot, backup);
            try { Directory.CreateSymbolicLink(candidateRoot, backup); }
            catch (Exception) { Directory.Move(backup, candidateRoot); }
        });
        if (!Directory.Exists(candidateRoot) || !Directory.Exists(backup))
            return; // symlink unsupported: fixture restored to ordinary dir
        Assert.Equal(PublishedRunValidationStatus.Invalid, r.Status);
    }

    [Fact]
    public void TypeTaxonomy_CandidateRootFile_AndFinalFile_Invalid()
    {
        Directory.CreateDirectory(Runs);
        string candFile = Path.Combine(Runs, "sqlite-native-v1");
        File.WriteAllText(candFile, "not a dir");
        var r1 = PublishedRunValidator.Validate(Runs, Identity("tax-1"));
        Assert.Equal(PublishedRunValidationStatus.Invalid, r1.Status);

        Directory.CreateDirectory(Path.Combine(Runs, "other-native"));
        string finalFile = Path.Combine(Runs, "other-native", "tax-final");
        File.WriteAllText(finalFile, "file not dir");
        var r2 = PublishedRunValidator.Validate(Runs, new RunIdentity
        {
            EvidenceSchemaVersion = EvidenceSchema.Version,
            ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
            CandidateId = "other-native",
            CandidateConfigId = CandidateAIdentity.CandidateConfigId,
            WorkloadId = CandidateAIdentity.WorkloadId,
            CorpusId = CandidateAIdentity.CorpusId,
            RunId = "tax-final",
        });
        Assert.Equal(PublishedRunValidationStatus.Invalid, r2.Status);
    }

    [Fact]
    public void ArtifactReplacedByDirectory_Invalid()
    {
        var id = Publish("dir-art");
        string payload = Path.Combine(Final(id), "analytical", "A1", "result.json");
        File.Delete(payload);
        Directory.CreateDirectory(payload);
        var r = PublishedRunValidator.Validate(Runs, id);
        Assert.Equal(PublishedRunValidationStatus.Invalid, r.Status);
    }

    [Fact]
    public void FinalRecheck_LateOrphanDirectory_Invalid()
    {
        var id = Publish("recheck-dir");
        var r = PublishedRunValidator.ValidateForTest(Runs, id, probe: null, cp =>
        {
            if (cp == PublishedValidatorCheckpoint.BeforeFinalConsistencyRecheck)
                Directory.CreateDirectory(Path.Combine(Final(id), "orphan-late"));
        });
        Assert.Equal(PublishedRunValidationStatus.Invalid, r.Status);
    }

    [Fact]
    public void FinalRecheck_LateSymlink_Invalid_WhereSupported()
    {
        var id = Publish("recheck-link");
        var r = PublishedRunValidator.ValidateForTest(Runs, id, probe: null, cp =>
        {
            if (cp == PublishedValidatorCheckpoint.BeforeFinalConsistencyRecheck)
            {
                string outside = Path.Combine(_root, "late-target.txt");
                File.WriteAllText(outside, "x");
                try { File.CreateSymbolicLink(Path.Combine(Final(id), "late-link.txt"), outside); } catch { }
            }
        });
        if (File.Exists(Path.Combine(Final(id), "late-link.txt")))
            Assert.Equal(PublishedRunValidationStatus.Invalid, r.Status);
    }

    [Fact]
    public void FinalRecheck_InspectionFailure_Error()
    {
        var id = Publish("recheck-err");
        bool fail = false;
        var r = PublishedRunValidator.ValidateForTest(Runs, id,
            path => fail && EvidencePathSafety.IsSamePath(path, Path.Combine(Runs, id.CandidateId))
                ? NodeKind.InspectionError
                : PublishedRunValidator.InspectNode(path),
            cp => { if (cp == PublishedValidatorCheckpoint.BeforeFinalConsistencyRecheck) fail = true; });
        Assert.Equal(PublishedRunValidationStatus.Error, r.Status);
    }

    [Fact]
    public void ReservedControlNamespace_StructuralRejected()
    {
        foreach (var extra in new[] { ".state-tmp-fake.json", "run.json/child" })
        {
            var id = Publish("res-" + Guid.NewGuid().ToString("N")[..6]);
            string mp = Path.Combine(Final(id), "evidence.manifest.json");
            var parsed = EvidenceJson.ReadManifest(File.ReadAllBytes(mp));
            var tampered = parsed with { Artifacts = parsed.Artifacts
                .Append(new ManifestArtifact(extra, 1, "a".PadRight(64, 'a'))).ToList() };
            File.WriteAllBytes(mp, EvidenceJson.SerializeManifest(tampered));
            Assert.Equal(PublishedRunValidationStatus.Invalid, PublishedRunValidator.Validate(Runs, id).Status);
        }
    }
    [Fact]
    public void LazyEnumerationFailure_Error_NoEscape()
    {
        var id = Publish("enum-fail");
        string final = Final(id);
        var r = PublishedRunValidator.ValidateForTest(Runs, id, probe: null, checkpoint: null,
            enumerate: path => EvidencePathSafety.IsSamePath(path, final)
                ? FaultyEnumeration(path)
                : Directory.EnumerateFileSystemEntries(path));
        Assert.Equal(PublishedRunValidationStatus.Error, r.Status);
    }

    private static IEnumerable<string> FaultyEnumeration(string root)
    {
        yield return Path.Combine(root, "run.state.json");
        throw new IOException("injected enumeration failure");
    }
}

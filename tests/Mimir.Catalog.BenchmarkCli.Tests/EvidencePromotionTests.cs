using System.Text;
using System.Text.Json;
using Mimir.Catalog.BenchmarkCli.Evidence;
using Mimir.Catalog.BenchmarkCli.Protocol;

namespace Mimir.Catalog.BenchmarkCli.Tests;

public class EvidenceStateTests
{
    [Fact]
    public void State_DeterministicSnakeWire_NoBom_TrailingLf()
    {
        byte[] a = EvidenceState.Serialize("Running", "run-1", "cand", "create");
        byte[] b = EvidenceState.Serialize("Running", "run-1", "cand", "create");
        Assert.Equal(a, b);
        Assert.NotEqual(0xEF, a[0]);
        Assert.Equal(0x0A, a[^1]);
        string text = Encoding.UTF8.GetString(a);
        Assert.Contains("\"run_id\":\"run-1\"", text);
        Assert.Contains("\"candidate_id\":\"cand\"", text);
        var snap = EvidenceState.ParseStrict(a);
        Assert.Equal("Running", snap.State);
        Assert.Equal("run-1", snap.RunId);
    }

    [Fact]
    public void State_DuplicateProperty_Rejected()
    {
        string json = Encoding.UTF8.GetString(EvidenceState.Serialize("Running", "r", "c"))
            .Replace("\"state\":\"Running\",", "\"state\":\"Running\",\"state\":\"Running\",", StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => EvidenceState.ParseStrict(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void State_UnknownAndMissing_Rejected()
    {
        string json = Encoding.UTF8.GetString(EvidenceState.Serialize("Running", "r", "c"));
        string unknown = json.Replace("{\"state\"", "{\"unknown\":1,\"state\"", StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => EvidenceState.ParseStrict(Encoding.UTF8.GetBytes(unknown)));
        string missing = json.Replace(",\"run_id\":\"r\"", "", StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => EvidenceState.ParseStrict(Encoding.UTF8.GetBytes(missing)));
    }

    [Fact]
    public void State_InvalidUtc_Rejected()
    {
        string json = "{\"state\":\"Complete\",\"run_id\":\"r\",\"candidate_id\":\"c\",\"utc\":\"not-a-date\"}\n";
        Assert.ThrowsAny<JsonException>(() => EvidenceState.ParseStrict(Encoding.UTF8.GetBytes(json)));
    }
}

public class EvidencePromotionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mimir-prom-" + Guid.NewGuid().ToString("N"));

    public EvidencePromotionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Runs => Path.Combine(_root, "runs");

    private RunIdentity Identity(string runId = "run-promote") => new()
    {
        EvidenceSchemaVersion = EvidenceSchema.Version,
        ProtocolVersion = ProtocolConstants.ChildProtocolVersion,
        CandidateId = "sqlite-native-v1",
        CandidateConfigId = CandidateAIdentity.CandidateConfigId,
        WorkloadId = CandidateAIdentity.WorkloadId,
        CorpusId = CandidateAIdentity.CorpusId,
        RunId = runId,
    };

    private (EvidenceStagingSession Session, EvidenceFinalizationResult Ready) Ready(string runId = "run-promote")
    {
        Directory.CreateDirectory(Runs);
        var s = EvidenceStagingSession.Create(Runs, Identity(runId));
        s.WriteText("payload/result.json", "{\"ok\":true}");
        var r = EvidenceFinalizer.Finalize(s);
        Assert.Equal(EvidenceFinalizationStatus.ReadyForPromotion, r.Status);
        return (s, r);
    }

    [Fact]
    public void Readiness_RunningValid_CompleteExpectedRejects()
    {
        var (s, _) = Ready();
        var ok = EvidenceReadinessValidator.Validate(s, EvidenceExpectedState.Running);
        Assert.True(ok.IsValid);
        var wrong = EvidenceReadinessValidator.Validate(s, EvidenceExpectedState.Complete);
        Assert.False(wrong.IsValid);
        Assert.Contains(wrong.Problems, p => p.Contains("Running"));
    }

    [Fact]
    public void Readiness_CompleteStaging_ValidWhenExpectedComplete()
    {
        var (s, _) = Ready();
        s.WriteCompleteState();
        var ok = EvidenceReadinessValidator.Validate(s, EvidenceExpectedState.Complete);
        Assert.True(ok.IsValid);
    }

    [Fact]
    public void Readiness_MutationAfterFinalize_Invalid()
    {
        var (s, _) = Ready();
        File.WriteAllText(Path.Combine(s.StagingPath, "run.json"), "{\"broken\"");
        var r = EvidenceReadinessValidator.Validate(s, EvidenceExpectedState.Running);
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Promote_TokenStatusRequired()
    {
        var (s, _) = Ready();
        var bad = new EvidenceFinalizationResult
        {
            Status = EvidenceFinalizationStatus.Failed,
            StagingPath = s.StagingPath,
            FinalPath = s.FinalPath,
            RunJson = null,
        };
        var r = EvidencePromoter.Promote(s, bad);
        Assert.Equal(EvidencePromotionStatus.FailedBeforeMove, r.Status);
        Assert.True(Directory.Exists(s.StagingPath));
    }

    [Fact]
    public void Promote_MismatchedPathsAndIdentity_Rejected()
    {
        var (s, _) = Ready();
        var wrongPath = new EvidenceFinalizationResult
        {
            Status = EvidenceFinalizationStatus.ReadyForPromotion,
            StagingPath = Path.Combine(Runs, "else.staging"),
            FinalPath = s.FinalPath,
            RunJson = ToRunJson(s.Identity),
        };
        Assert.Equal(EvidencePromotionStatus.FailedBeforeMove,
            EvidencePromoter.Promote(s, wrongPath).Status);

        var wrongIdentity = new EvidenceFinalizationResult
        {
            Status = EvidenceFinalizationStatus.ReadyForPromotion,
            StagingPath = s.StagingPath,
            FinalPath = s.FinalPath,
            RunJson = ToRunJson(s.Identity) with { RunId = "other-run" },
        };
        Assert.Equal(EvidencePromotionStatus.FailedBeforeMove,
            EvidencePromoter.Promote(s, wrongIdentity).Status);
    }

    [Fact]
    public void Promote_FreshMutationAfterFinalize_BlocksBeforeComplete()
    {
        var (s, _) = Ready();
        File.WriteAllText(Path.Combine(s.StagingPath, "payload", "result.json"), "{\"tampered\":true}");
        var r = EvidencePromoter.Promote(s, ReadyToken(s));
        Assert.Equal(EvidencePromotionStatus.FailedBeforeMove, r.Status);
        Assert.True(Directory.Exists(s.StagingPath));
        Assert.False(Directory.Exists(s.FinalPath));
        var snap = EvidenceState.ParseStrict(File.ReadAllBytes(Path.Combine(s.StagingPath, "run.state.json")));
        Assert.Equal("Failed", snap.State);
    }

    [Fact]
    public void Promote_Success_PublishedStagingGone_FinalComplete()
    {
        var (s, r) = Ready();
        var result = EvidencePromoter.Promote(s, r);
        Assert.Equal(EvidencePromotionStatus.Published, result.Status);
        Assert.Equal(s.FinalPath, result.PublishedPath);
        Assert.False(Directory.Exists(s.StagingPath));
        Assert.True(Directory.Exists(s.FinalPath));
        Assert.True(File.Exists(Path.Combine(s.FinalPath, "evidence.manifest.json")));
        string state = File.ReadAllText(Path.Combine(s.FinalPath, "run.state.json"));
        Assert.Contains("\"state\":\"Complete\"", state);
    }

    [Fact]
    public void Promote_ExistingFinalBlocksPreMove_NoComplete()
    {
        var (s, r) = Ready();
        Directory.CreateDirectory(s.FinalPath);
        File.WriteAllText(Path.Combine(s.FinalPath, "occupant.txt"), "keep");
        var result = EvidencePromoter.Promote(s, r);
        Assert.Equal(EvidencePromotionStatus.FailedBeforeMove, result.Status);
        Assert.True(Directory.Exists(s.StagingPath));
        Assert.True(File.Exists(Path.Combine(s.FinalPath, "occupant.txt")));
        var snap = EvidenceState.ParseStrict(File.ReadAllBytes(Path.Combine(s.StagingPath, "run.state.json")));
        Assert.Equal("Failed", snap.State);
    }

    [Fact]
    public void Promote_ThrowNoChange_MoveFailedStagingRetained()
    {
        var (s, r) = Ready();
        var result = EvidencePromoter.PromoteForTest(s, r, (_, _) => throw new IOException("boom"));
        Assert.Equal(EvidencePromotionStatus.MoveFailedStagingRetained, result.Status);
        Assert.True(result.StagingExists);
        Assert.False(result.FinalExists);
        Assert.True(Directory.Exists(s.StagingPath));
    }

    [Fact]
    public void Promote_ThrowCollision_MoveFailedStagingRetained_FinalUntouched()
    {
        var (s, r) = Ready();
        string final = s.FinalPath;
        var result = EvidencePromoter.PromoteForTest(s, r, (_, _) =>
        {
            Directory.CreateDirectory(final);
            File.WriteAllText(Path.Combine(final, "x.txt"), "pre-existing");
            throw new IOException("collision");
        });
        Assert.Equal(EvidencePromotionStatus.MoveFailedStagingRetained, result.Status);
        Assert.True(result.StagingExists);
        Assert.True(result.FinalExists);
        Assert.Equal("pre-existing", File.ReadAllText(Path.Combine(final, "x.txt")));
    }

    [Fact]
    public void Promote_ThrowAfterRealMove_Ambiguous()
    {
        var (s, r) = Ready();
        var result = EvidencePromoter.PromoteForTest(s, r, (from, to) =>
        {
            Directory.Move(from, to);
            throw new IOException("reported failure after rename");
        });
        Assert.Equal(EvidencePromotionStatus.AmbiguousFilesystemState, result.Status);
        Assert.False(result.StagingExists);
        Assert.True(result.FinalExists);
    }

    [Fact]
    public void Promote_ThrowBothAbsent_Ambiguous()
    {
        var (s, r) = Ready();
        var result = EvidencePromoter.PromoteForTest(s, r, (from, _) =>
        {
            Directory.Delete(from, recursive: true);
            throw new IOException("lost staging");
        });
        Assert.Equal(EvidencePromotionStatus.AmbiguousFilesystemState, result.Status);
        Assert.False(result.StagingExists);
        Assert.False(result.FinalExists);
    }

    [Fact]
    public void CandidateRootSymlink_BlocksSessionCreation()
    {
        Directory.CreateDirectory(Runs);
        string real = Path.Combine(_root, "real-candidate");
        Directory.CreateDirectory(real);
        string link = Path.Combine(Runs, "sqlite-native-v1");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception)
        {
            return;
        }
        Assert.Throws<EvidenceStagingException>(() => EvidenceStagingSession.Create(Runs, Identity()));
    }

    [Fact]
    public void CandidateRootReplacedBeforePromotion_Blocks()
    {
        var (s, r) = Ready();
        string candidateRoot = s.Layout.CandidateRoot;
        string backup = Path.Combine(_root, "candidate-backup");
        Directory.Move(candidateRoot, backup);
        try
        {
            Directory.CreateSymbolicLink(candidateRoot, backup);
        }
        catch (Exception)
        {
            Directory.Move(backup, candidateRoot);
            return;
        }
        var result = EvidencePromoter.Promote(s, r);
        Assert.Equal(EvidencePromotionStatus.FailedBeforeMove, result.Status);
        Assert.Contains(result.Problems, p => p.Contains("candidate root"));
    }

    private EvidenceFinalizationResult ReadyToken(EvidenceStagingSession s) => new()
    {
        Status = EvidenceFinalizationStatus.ReadyForPromotion,
        StagingPath = s.StagingPath,
        FinalPath = s.FinalPath,
        RunJson = ToRunJson(s.Identity),
        Problems = Array.Empty<string>(),
    };

    private static EvidenceRunJson ToRunJson(RunIdentity id) => new(
        id.EvidenceSchemaVersion, id.ProtocolVersion, id.CandidateId, id.CandidateConfigId,
        id.WorkloadId, id.CorpusId, id.RunId);
    [Fact]
    public void Finalizer_ResultUsesReadinessFacts_NoPostRead()
    {
        var (session, ready) = Ready();
        var readiness = EvidenceReadinessValidator.Validate(session, EvidenceExpectedState.Running);
        Assert.True(readiness.IsValid);
        Assert.Equal(readiness.RunJson, ready.RunJson); // record value equality
        Assert.Equal(readiness.ManifestBytes, ready.ManifestBytes);
        Assert.Equal(readiness.ManifestSha256, ready.ManifestSha256);
        Assert.Equal(EvidenceControlWriter.Sha256(readiness.ManifestBytes!), ready.ManifestSha256);
    }

    private static void WriteState(EvidenceStagingSession session, string state, string? stage = "promote", string? reason = null, DateTime? utc = null)
    {
        File.WriteAllBytes(Path.Combine(session.StagingPath, "run.state.json"),
            EvidenceState.Serialize(state, session.Identity.RunId, session.Identity.CandidateId, stage, reason, utc));
    }

    [Fact]
    public void CompleteCanonical_PassesSemantics()
    {
        var (session, _) = Ready();
        session.WriteCompleteState();
        Assert.True(EvidenceReadinessValidator.Validate(session, EvidenceExpectedState.Complete).IsValid);
    }

    [Fact]
    public void Complete_SemanticRejections()
    {
        var (session, _) = Ready();
        WriteState(session, "Complete", stage: null, reason: null, utc: DateTime.UtcNow);
        Assert.False(EvidenceReadinessValidator.Validate(session, EvidenceExpectedState.Complete).IsValid);
        WriteState(session, "Complete", stage: "bogus", reason: null, utc: DateTime.UtcNow);
        Assert.False(EvidenceReadinessValidator.Validate(session, EvidenceExpectedState.Complete).IsValid);
        WriteState(session, "Complete", stage: "promote", reason: "why", utc: DateTime.UtcNow);
        Assert.False(EvidenceReadinessValidator.Validate(session, EvidenceExpectedState.Complete).IsValid);
        WriteState(session, "Complete", stage: "promote", reason: null, utc: null);
        Assert.False(EvidenceReadinessValidator.Validate(session, EvidenceExpectedState.Complete).IsValid);
    }
}

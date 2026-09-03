namespace Mimir.Catalog.BenchmarkCli.Evidence;

public enum EvidencePromotionStatus
{
    Published,
    FailedBeforeMove,
    MoveFailedStagingRetained,
    AmbiguousFilesystemState,
}

public sealed class EvidencePromotionResult
{
    public required EvidencePromotionStatus Status { get; init; }
    public required bool StagingExists { get; init; }
    public required bool FinalExists { get; init; }
    public string? PublishedPath { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Atomic promotion: Complete write then Directory.Move(staging, final).
/// Publication is decided by the rename returning successfully; a later read
/// failure never downgrades Published. Promotion status stays orthogonal to
/// benchmark/process/resource statuses.
/// </summary>
public static class EvidencePromoter
{
    public static EvidencePromotionResult Promote(EvidenceStagingSession session, EvidenceFinalizationResult finalization)
        => PromoteCore(session, finalization, (from, to) => Directory.Move(from, to));

    internal static EvidencePromotionResult PromoteForTest(
        EvidenceStagingSession session,
        EvidenceFinalizationResult finalization,
        Action<string, string> move)
        => PromoteCore(session, finalization, move);

    private static EvidencePromotionResult PromoteCore(
        EvidenceStagingSession session,
        EvidenceFinalizationResult finalization,
        Action<string, string> move)
    {
        var problems = new List<string>();

        // Token precondition.
        string? tokenError = ValidateToken(session, finalization);
        if (tokenError is not null)
        {
            problems.Add(tokenError);
            return FailSafe(session, problems);
        }

        // Pre-Complete gate: fresh Running readiness.
        var running = EvidenceReadinessValidator.Validate(session, EvidenceExpectedState.Running);
        if (!running.IsValid)
        {
            problems.Add("pre-Complete readiness failed: " + string.Join("; ", running.Problems));
            return FailSafe(session, problems);
        }

        // Complete is the last intended successful staging mutation.
        try
        {
            session.WriteCompleteState();
        }
        catch (Exception ex)
        {
            problems.Add($"failed to write Complete state: {ex.Message}");
            return FailSafe(session, problems);
        }

        // Post-Complete gate: fresh Complete readiness + final absent.
        var complete = EvidenceReadinessValidator.Validate(session, EvidenceExpectedState.Complete);
        if (!complete.IsValid)
        {
            problems.Add("post-Complete readiness failed: " + string.Join("; ", complete.Problems));
            problems.Add("run remains unpublished (Complete left in staging is never published)");
            return FailSafe(session, problems);
        }

        // Atomic publication boundary.
        try
        {
            move(session.StagingPath, session.FinalPath);
        }
        catch (Exception ex)
        {
            problems.Add($"Directory.Move failed: {ex.Message}");
            return ClassifyMoveFailure(session, problems);
        }

        return new EvidencePromotionResult
        {
            Status = EvidencePromotionStatus.Published,
            StagingExists = Directory.Exists(session.StagingPath),
            FinalExists = Directory.Exists(session.FinalPath),
            PublishedPath = session.FinalPath,
            Problems = Array.Empty<string>(),
        };
    }

    private static string? ValidateToken(EvidenceStagingSession session, EvidenceFinalizationResult finalization)
    {
        if (finalization.Status != EvidenceFinalizationStatus.ReadyForPromotion)
            return "finalization result is not ReadyForPromotion";
        if (!EvidencePathSafety.IsSamePath(finalization.StagingPath, session.StagingPath))
            return "finalization staging path does not match session staging path";
        if (!EvidencePathSafety.IsSamePath(finalization.FinalPath, session.FinalPath))
            return "finalization final path does not match session final path";
        var run = finalization.RunJson;
        if (run is null)
            return "finalization carries no run.json identity";
        var id = session.Identity;
        if (run.ProtocolVersion != id.ProtocolVersion || run.CandidateId != id.CandidateId
            || run.CandidateConfigId != id.CandidateConfigId || run.WorkloadId != id.WorkloadId
            || run.CorpusId != id.CorpusId || run.RunId != id.RunId
            || run.EvidenceSchemaVersion != id.EvidenceSchemaVersion)
            return "finalization run identity does not match session identity";
        return null;
    }

    private static EvidencePromotionResult FailSafe(EvidenceStagingSession session, List<string> problems)
    {
        problems.AddRange(TrySafeFail(session, "promote", problems.LastOrDefault() ?? "promotion gate failure"));
        return new EvidencePromotionResult
        {
            Status = EvidencePromotionStatus.FailedBeforeMove,
            StagingExists = Directory.Exists(session.StagingPath),
            FinalExists = Directory.Exists(session.FinalPath),
            Problems = problems,
        };
    }

    private static EvidencePromotionResult ClassifyMoveFailure(EvidenceStagingSession session, List<string> problems)
    {
        bool stagingExists = Directory.Exists(session.StagingPath);
        bool finalExists = Directory.Exists(session.FinalPath) || File.Exists(session.FinalPath);
        if (stagingExists && !finalExists)
        {
            problems.AddRange(TrySafeFail(session, "promote", "move failed; staging retained"));
            return new EvidencePromotionResult
            {
                Status = EvidencePromotionStatus.MoveFailedStagingRetained,
                StagingExists = true,
                FinalExists = false,
                Problems = problems,
            };
        }
        if (stagingExists && finalExists)
        {
            problems.Add("move failed: final destination exists and was never touched");
            problems.AddRange(TrySafeFail(session, "promote", "move collision; staging retained"));
            return new EvidencePromotionResult
            {
                Status = EvidencePromotionStatus.MoveFailedStagingRetained,
                StagingExists = true,
                FinalExists = true,
                Problems = problems,
            };
        }
        // staging absent (+/- final) is ambiguous filesystem state
        problems.Add(finalExists
            ? "staging absent but final exists after a failed move; ambiguous filesystem state"
            : "staging and final both absent after a failed move; ambiguous filesystem state");
        return new EvidencePromotionResult
        {
            Status = EvidencePromotionStatus.AmbiguousFilesystemState,
            StagingExists = stagingExists,
            FinalExists = finalExists,
            Problems = problems,
        };
    }

    private static IReadOnlyList<string> TrySafeFail(EvidenceStagingSession session, string stage, string reason)
    {
        string staging = session.StagingPath;
        bool safe;
        try
        {
            safe = Directory.Exists(staging) && !File.Exists(staging) && !EvidenceTreeInspector.IsSymlinkOrReparse(staging);
        }
        catch
        {
            safe = false;
        }
        if (!safe)
            return new[] { $"Failed state not written because staging root is unsafe/unavailable ({stage})" };
        return session.Fail(stage, Sanitize(reason));
    }

    private static string Sanitize(string message) => message.Replace('\n', ' ').Replace('\r', ' ');
}

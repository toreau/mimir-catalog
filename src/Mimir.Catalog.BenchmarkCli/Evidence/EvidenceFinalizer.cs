using System.Security.Cryptography;

namespace Mimir.Catalog.BenchmarkCli.Evidence;

public enum EvidenceFinalizationStatus
{
    ReadyForPromotion,
    Failed,
}

public sealed class EvidenceFinalizationResult
{
    public required EvidenceFinalizationStatus Status { get; init; }
    public required string StagingPath { get; init; }
    public required string FinalPath { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public EvidenceRunJson? RunJson { get; init; }
    public byte[]? ManifestBytes { get; init; }
    public string? ManifestSha256 { get; init; }
}

internal sealed class FinalizeStageException : Exception
{
    public string Stage { get; }
    public FinalizeStageException(string stage, string message) : base(message) => Stage = stage;
}

/// <summary>
/// Pre-promotion finalization (1c.2a): run.json + evidence.manifest.json +
/// whole-tree validation. Never writes Complete, never moves directories; the
/// only success is an in-memory ReadyForPromotion result.
/// </summary>
public static class EvidenceFinalizer
{
    public static EvidenceFinalizationResult Finalize(EvidenceStagingSession session)
        => FinalizeCore(session, hook: null);

    internal static EvidenceFinalizationResult FinalizeForTest(
        EvidenceStagingSession session,
        Action<EvidenceFinalizeCheckpoint> hook)
        => FinalizeCore(session, hook);

    internal enum EvidenceFinalizeCheckpoint
    {
        BeforeFinalControlVerification,
    }

    private static EvidenceFinalizationResult FinalizeCore(
        EvidenceStagingSession session,
        Action<EvidenceFinalizeCheckpoint>? hook)
    {
        var problems = new List<string>();
        try
        {
            return RunFinalization(session, hook);
        }
        catch (FinalizeStageException ex)
        {
            problems.Add($"{ex.Stage}: {ex.Message}");
            problems.AddRange(TrySafeFail(session, ex.Stage, ex.Message));
            return FailedResult(session, problems);
        }
        catch (Exception ex)
        {
            problems.Add($"finalize:internal: {ex.Message}");
            problems.AddRange(TrySafeFail(session, "finalize:internal", ex.Message));
            return FailedResult(session, problems);
        }
    }

    private static EvidenceFinalizationResult FailedResult(EvidenceStagingSession session, List<string> problems)
        => new()
        {
            Status = EvidenceFinalizationStatus.Failed,
            StagingPath = session.StagingPath,
            FinalPath = session.FinalPath,
            Problems = problems,
        };

    private static IReadOnlyList<string> TrySafeFail(EvidenceStagingSession session, string stage, string reason)
    {
        if (!StagingRootSafe(session.StagingPath))
            return new[] { $"Failed state not written because staging root is unsafe/unavailable ({stage})" };
        return session.Fail(stage, Sanitize(reason));
    }

    private static bool StagingRootSafe(string path)
    {
        try
        {
            return Directory.Exists(path) && !File.Exists(path) && !EvidenceTreeInspector.IsSymlinkOrReparse(path);
        }
        catch
        {
            return false;
        }
    }

    private static EvidenceFinalizationResult RunFinalization(EvidenceStagingSession session, Action<EvidenceFinalizeCheckpoint>? hook)
    {
        string staging = session.StagingPath;
        string final = session.FinalPath;
        var identity = session.Identity;

        // 1. staging root/tree integrity
        TreeView tree;
        try
        {
            tree = EvidenceTreeInspector.Inspect(staging);
        }
        catch (Exception ex)
        {
            throw new FinalizeStageException("finalize:tree", ex.Message);
        }

        // 2. registered-artifact snapshots
        List<string> registeredProblems;
        try
        {
            registeredProblems = session.VerifyRegisteredArtifacts().ToList();
        }
        catch (Exception ex)
        {
            throw new FinalizeStageException("finalize:registered", ex.Message);
        }
        if (registeredProblems.Count > 0)
            throw new FinalizeStageException("finalize:registered", string.Join("; ", registeredProblems));

        // 3. exact pre-run.json inventory
        EnsureInventory(staging, tree, session, additionalFiles: null, "finalize:inventory");

        // 4. final destination absent (readiness)
        EnsureFinalAbsent(final, "finalize:final-destination");

        // 5. run.json create once
        byte[] runBytes = EvidenceJson.SerializeRunJson(ToRunJson(identity));
        CreateControlOnce(staging, EvidenceStagingSession.RunJsonName, runBytes, "finalize:run-json");
        byte[] runOnDisk = File.ReadAllBytes(Path.Combine(staging, EvidenceStagingSession.RunJsonName));
        string runSha = EvidenceControlWriter.Sha256(runOnDisk);
        var runJsonEntry = new ManifestArtifact(EvidenceStagingSession.RunJsonName, runOnDisk.Length, runSha);

        // 6. registered snapshots + run.json entry → deterministic manifest
        EvidenceManifest manifest = EvidenceManifestBuilder.Build(identity, session.RegisteredArtifacts, runJsonEntry);

        // 7. manifest create once
        byte[] manifestBytes = EvidenceJson.SerializeManifest(manifest);
        CreateControlOnce(staging, EvidenceStagingSession.ManifestName, manifestBytes, "finalize:manifest");
        byte[] manifestOnDisk = File.ReadAllBytes(Path.Combine(staging, EvidenceStagingSession.ManifestName));
        string manifestSha = EvidenceControlWriter.Sha256(manifestOnDisk);

        // 8-11. strict rereads + identity + semantic validation
        EvidenceRunJson runParsed;
        EvidenceManifest manifestParsed;
        try
        {
            runParsed = EvidenceJson.ReadRunJson(runOnDisk);
            manifestParsed = EvidenceJson.ReadManifest(manifestOnDisk);
        }
        catch (Exception ex)
        {
            throw new FinalizeStageException("finalize:strict-validate", ex.Message);
        }
        ValidateRunIdentity(identity, runParsed, manifestParsed);
        ValidateManifestSemantics(session, manifestParsed, manifestOnDisk.Length, manifestSha);

        // 12. re-inspect tree; 13. re-verify registered; 14. exact final inventory
        TreeView finalTree;
        try
        {
            finalTree = EvidenceTreeInspector.Inspect(staging);
        }
        catch (Exception ex)
        {
            throw new FinalizeStageException("finalize:tree", ex.Message);
        }
        var recheck = session.VerifyRegisteredArtifacts();
        if (recheck.Count > 0)
            throw new FinalizeStageException("finalize:registered", string.Join("; ", recheck));
        EnsureInventory(staging, finalTree, session,
            additionalFiles: new[] { EvidenceStagingSession.RunJsonName, EvidenceStagingSession.ManifestName },
            "finalize:inventory");

        // 15. every manifest entry matches actual current bytes/hash
        VerifyManifestEntriesAgainstDisk(session, manifestParsed, runOnDisk, runSha, finalTree);

        // 16. deterministic test checkpoint before the authoritative fresh reread
        hook?.Invoke(EvidenceFinalizeCheckpoint.BeforeFinalControlVerification);

        // 17. authoritative final fresh reread of both immutable control files
        byte[] runFresh = ReadControlBytes(staging, EvidenceStagingSession.RunJsonName, "finalize:strict-validate");
        byte[] manifestFresh = ReadControlBytes(staging, EvidenceStagingSession.ManifestName, "finalize:strict-validate");
        EvidenceRunJson runParsedFresh;
        EvidenceManifest manifestParsedFresh;
        try
        {
            runParsedFresh = EvidenceJson.ReadRunJson(runFresh);
            manifestParsedFresh = EvidenceJson.ReadManifest(manifestFresh);
        }
        catch (Exception ex)
        {
            throw new FinalizeStageException("finalize:strict-validate", ex.Message);
        }
        ValidateRunIdentity(identity, runParsedFresh, manifestParsedFresh);
        ValidateManifestSemantics(session, manifestParsedFresh, manifestFresh.Length, "");

        // run.json manifest entry must match the FRESH disk bytes
        string runShaFresh = EvidenceControlWriter.Sha256(runFresh);
        var runFreshEntry = manifestParsedFresh.Artifacts.Single(x => x.RelativePath == EvidenceStagingSession.RunJsonName);
        if (runFreshEntry.Bytes != runFresh.Length || runFreshEntry.Sha256 != runShaFresh)
            throw new FinalizeStageException("finalize:strict-validate", "run.json manifest entry no longer matches fresh disk bytes");

        // every registered artifact re-checked against fresh disk state
        var freshProblems = session.VerifyRegisteredArtifacts();
        if (freshProblems.Count > 0)
            throw new FinalizeStageException("finalize:registered", string.Join("; ", freshProblems));

        // 18. final destination recheck
        EnsureFinalAbsent(final, "finalize:final-destination");

        // ReadyForPromotion (in memory only; state remains Running). Manifest
        // bytes/SHA are computed from the fresh final manifest bytes.
        string manifestShaFinal = EvidenceControlWriter.Sha256(manifestFresh);
        return new EvidenceFinalizationResult
        {
            Status = EvidenceFinalizationStatus.ReadyForPromotion,
            StagingPath = staging,
            FinalPath = final,
            RunJson = runParsedFresh,
            ManifestBytes = manifestFresh,
            ManifestSha256 = manifestShaFinal,
            Problems = Array.Empty<string>(),
        };
    }

    private static byte[] ReadControlBytes(string staging, string name, string stage)
    {
        try
        {
            return File.ReadAllBytes(Path.Combine(staging, name));
        }
        catch (Exception ex)
        {
            throw new FinalizeStageException(stage, $"failed to reread {name}: {ex.Message}");
        }
    }

    private static EvidenceRunJson ToRunJson(RunIdentity id) => new(
        id.EvidenceSchemaVersion, id.ProtocolVersion, id.CandidateId, id.CandidateConfigId,
        id.WorkloadId, id.CorpusId, id.RunId);

    private static void ValidateRunIdentity(RunIdentity identity, EvidenceRunJson run, EvidenceManifest manifest)
    {
        if (identity.EvidenceSchemaVersion != EvidenceSchema.Version
            || run.EvidenceSchemaVersion != EvidenceSchema.Version
            || manifest.EvidenceSchemaVersion != EvidenceSchema.Version)
            throw new FinalizeStageException("finalize:strict-validate", "unsupported evidence schema version");
        var mismatches = new List<string>();
        if (run.ProtocolVersion != identity.ProtocolVersion) mismatches.Add("run.json protocol_version");
        if (run.CandidateId != identity.CandidateId || manifest.CandidateId != identity.CandidateId) mismatches.Add("candidate_id");
        if (run.CandidateConfigId != identity.CandidateConfigId || manifest.CandidateConfigId != identity.CandidateConfigId) mismatches.Add("candidate_config_id");
        if (run.WorkloadId != identity.WorkloadId || manifest.WorkloadId != identity.WorkloadId) mismatches.Add("workload_id");
        if (run.CorpusId != identity.CorpusId || manifest.CorpusId != identity.CorpusId) mismatches.Add("corpus_id");
        if (run.RunId != identity.RunId || manifest.RunId != identity.RunId) mismatches.Add("run_id");
        if (mismatches.Count > 0)
            throw new FinalizeStageException("finalize:strict-validate", "identity mismatch: " + string.Join(", ", mismatches));
    }

    private static void ValidateManifestSemantics(EvidenceStagingSession session, EvidenceManifest manifest, long manifestBytes, string manifestSha)
    {
        _ = manifestBytes; _ = manifestSha;
        if (manifest.Artifacts is null) throw new FinalizeStageException("finalize:strict-validate", "manifest artifacts missing");
        var registered = session.RegisteredArtifacts;
        var registeredPaths = registered.Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal);
        var expected = registeredPaths.Append(EvidenceStagingSession.RunJsonName).ToHashSet(StringComparer.Ordinal);

        var actual = new HashSet<string>(StringComparer.Ordinal);
        string? prev = null;
        foreach (var a in manifest.Artifacts)
        {
            if (!EvidencePathSafety.TryValidateArtifactPath(a.RelativePath, out _))
                throw new FinalizeStageException("finalize:strict-validate", $"invalid artifact path '{a.RelativePath}'");
            if (!expected.Contains(a.RelativePath))
                throw new FinalizeStageException("finalize:strict-validate", $"manifest artifact outside expected set: '{a.RelativePath}'");
            if (!actual.Add(a.RelativePath))
                throw new FinalizeStageException("finalize:strict-validate", $"duplicate artifact '{a.RelativePath}'");
            if (prev is not null && string.CompareOrdinal(prev, a.RelativePath) >= 0)
                throw new FinalizeStageException("finalize:strict-validate", "manifest artifacts not strictly ordinal-sorted");
            prev = a.RelativePath;
            if (a.Bytes < 0) throw new FinalizeStageException("finalize:strict-validate", "negative artifact bytes");
            if (!EvidenceJson.IsValidSha256(a.Sha256))
                throw new FinalizeStageException("finalize:strict-validate", $"malformed sha256 for '{a.RelativePath}'");
        }
        if (!actual.SetEquals(expected))
            throw new FinalizeStageException("finalize:strict-validate", "manifest artifact set does not equal registered + run.json");

        // registered snapshot facts match manifest entries
        foreach (var e in registered)
        {
            var m = manifest.Artifacts.Single(x => x.RelativePath == e.RelativePath);
            if (m.Bytes != e.Bytes || m.Sha256 != e.Sha256)
                throw new FinalizeStageException("finalize:strict-validate", $"registered snapshot mismatch for '{e.RelativePath}'");
        }
    }

    private static void VerifyManifestEntriesAgainstDisk(EvidenceStagingSession session, EvidenceManifest manifest, byte[] runOnDisk, string runSha, TreeView tree)
    {
        // run.json
        var runEntry = manifest.Artifacts.Single(x => x.RelativePath == EvidenceStagingSession.RunJsonName);
        if (runEntry.Bytes != runOnDisk.Length || runEntry.Sha256 != runSha)
            throw new FinalizeStageException("finalize:strict-validate", "run.json manifest entry does not match disk");
        // registered artifacts rehashed from disk
        foreach (var e in session.RegisteredArtifacts)
        {
            string full = EvidencePathSafety.ResolveUnderRoot(session.StagingPath, e.RelativePath);
            if (!tree.Files.Contains(e.RelativePath)) continue; // missing already reported by verification above
            long len = new FileInfo(full).Length;
            string sha = EvidenceControlWriter.Sha256(File.ReadAllBytes(full));
            var m = manifest.Artifacts.Single(x => x.RelativePath == e.RelativePath);
            if (m.Bytes != len || m.Sha256 != sha)
                throw new FinalizeStageException("finalize:strict-validate", $"artifact '{e.RelativePath}' no longer matches its manifest entry");
        }
    }

    private static void CreateControlOnce(string staging, string name, byte[] bytes, string stage)
    {
        try
        {
            EvidenceControlWriter.WriteCreateNew(staging, name, bytes);
        }
        catch (EvidenceStagingException ex)
        {
            throw new FinalizeStageException(stage, ex.Message);
        }
    }

    private static void EnsureInventory(string staging, TreeView tree, EvidenceStagingSession session, IReadOnlyList<string>? additionalFiles, string stage)
    {
        var allowedFiles = session.RegisteredArtifacts.Select(e => e.RelativePath)
            .Append(EvidenceStagingSession.StateFileName).ToList();
        if (additionalFiles is not null) allowedFiles.AddRange(additionalFiles);
        var allowedSet = allowedFiles.ToHashSet(StringComparer.Ordinal);

        if (!tree.Files.ToHashSet(StringComparer.Ordinal).SetEquals(allowedSet))
            throw new FinalizeStageException(stage, $"unexpected/missing files under staging: {DescribeDiff(tree.Files, allowedSet)}");

        // Allowed directories: strict ancestors of allowed files (plus implicit root).
        var allowedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in allowedFiles)
        {
            var segments = f.Split('/');
            for (int i = 1; i < segments.Length; i++)
                allowedDirs.Add(string.Join('/', segments.Take(i)));
        }
        if (!tree.Directories.ToHashSet(StringComparer.Ordinal).SetEquals(allowedDirs))
            throw new FinalizeStageException(stage, $"unexpected/empty directories under staging: {string.Join(",", tree.Directories.Where(d => !allowedDirs.Contains(d)))}");
    }

    private static string DescribeDiff(IReadOnlyList<string> actual, HashSet<string> allowed)
    {
        var unexpected = actual.Where(f => !allowed.Contains(f)).ToList();
        var missing = allowed.Where(f => !actual.Contains(f)).ToList();
        return $"unexpected=[{string.Join(",", unexpected)}] missing=[{string.Join(",", missing)}]";
    }

    private static void EnsureFinalAbsent(string final, string stage)
    {
        if (File.Exists(final) || Directory.Exists(final))
            throw new FinalizeStageException(stage, $"final destination already exists and is preserved: {final}");
    }

    private static string Sanitize(string message) => message.Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>Result of a safe, link-free whole-tree walk.</summary>
public sealed class TreeView
{
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyList<string> Directories { get; init; }
}

/// <summary>
/// Safe recursive tree inspector: verifies each directory before recursion and
/// never follows a symlink/reparse point. Relative paths are canonical '/'.
/// </summary>
public static class EvidenceTreeInspector
{
    public static TreeView Inspect(string root)
    {
        if (!Directory.Exists(root))
            throw new EvidenceStagingException($"staging root missing: {root}");
        if (IsSymlinkOrReparse(root))
            throw new EvidenceStagingException($"staging root is a symlink/reparse point: {root}");

        var files = new List<string>();
        var dirs = new List<string>();
        Walk(root, "", files, dirs);
        files.Sort(StringComparer.Ordinal);
        dirs.Sort(StringComparer.Ordinal);
        return new TreeView { Files = files, Directories = dirs };
    }

    private static void Walk(string absolute, string relative, List<string> files, List<string> dirs)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(absolute);
        }
        catch (Exception ex)
        {
            throw new EvidenceStagingException($"failed to enumerate '{relative}': {ex.Message}");
        }

        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            string rel = relative.Length == 0 ? name : relative + "/" + name;
            bool isDir;
            try
            {
                isDir = Directory.Exists(entry);
            }
            catch (Exception ex)
            {
                throw new EvidenceStagingException($"cannot inspect '{rel}': {ex.Message}");
            }

            if (IsSymlinkOrReparse(entry))
                throw new EvidenceStagingException($"staging contains a symlink/reparse point: {rel}");
            if (isDir)
            {
                dirs.Add(rel);
                Walk(entry, rel, files, dirs);
            }
            else
            {
                if (!File.Exists(entry))
                    throw new EvidenceStagingException($"entry is not a readable file: {rel}");
                files.Add(rel);
            }
        }
    }

    public static bool IsSymlinkOrReparse(string path)
    {
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: false) is not null) return true;
            if (File.Exists(path))
            {
                var f = new FileInfo(path);
                return f.LinkTarget is not null || (f.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            if (Directory.Exists(path))
            {
                var d = new DirectoryInfo(path);
                return d.LinkTarget is not null || (d.Attributes & FileAttributes.ReparsePoint) != 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // treat un-inspectable entries as integrity failures
        }
        return false;
    }
}

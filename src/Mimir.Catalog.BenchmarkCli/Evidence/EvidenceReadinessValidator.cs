using System.Security.Cryptography;

namespace Mimir.Catalog.BenchmarkCli.Evidence;

public sealed class EvidenceReadinessResult
{
    public required bool IsValid { get; init; }
    public EvidenceRunJson? RunJson { get; init; }
    public EvidenceManifest? Manifest { get; init; }
    public byte[]? ManifestBytes { get; init; }
    public string? ManifestSha256 { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Shared read-only readiness validation over an already-finalized staging
/// session. Expected state (Running|Complete) is supplied explicitly and never
/// inferred from disk. Never writes; never creates/rewrites immutable files.
/// Problem messages carry stable category prefixes (finalize:tree,
/// finalize:registered, finalize:inventory, finalize:state,
/// finalize:strict-validate, finalize:final-destination).
/// </summary>
public enum EvidenceExpectedState { Running, Complete }

public static class EvidenceReadinessValidator
{
    public static EvidenceReadinessResult Validate(EvidenceStagingSession session, EvidenceExpectedState expectedState)
    {
        var problems = new List<string>();
        string staging = session.StagingPath;
        string final = session.FinalPath;
        var identity = session.Identity;

        // 1. CandidateRoot + staging root link safety
        string candidateRoot = session.Layout.CandidateRoot;
        if (!Directory.Exists(candidateRoot) || IsSymlink(candidateRoot))
            problems.Add("finalize:tree: candidate root missing or is a symlink/reparse point");
        if (!Directory.Exists(staging) || IsSymlink(staging))
            problems.Add("finalize:tree: staging root missing or is a symlink/reparse point");

        // 2. whole-tree
        TreeView? tree = null;
        try
        {
            tree = EvidenceTreeInspector.Inspect(staging);
        }
        catch (Exception ex)
        {
            problems.Add($"finalize:tree: {ex.Message}");
        }

        // 3. exact finalized inventory
        var additional = new[] { EvidenceStagingSession.RunJsonName, EvidenceStagingSession.ManifestName };
        if (tree is not null)
            CollectInventoryProblems(problems, session, tree, additional);

        // 4. registered artifact snapshots
        try
        {
            foreach (var p in session.VerifyRegisteredArtifacts())
                problems.Add($"finalize:registered: {p}");
        }
        catch (Exception ex)
        {
            problems.Add($"finalize:registered: {ex.Message}");
        }

        EvidenceRunJson? runParsed = null;
        EvidenceManifest? manifestParsed = null;
        byte[]? manifestBytes = null;
        string? manifestSha = null;

        // 5. strict state + expected state
        byte[]? stateBytes = TryRead(problems, staging, EvidenceStagingSession.StateFileName, "finalize:state");
        if (stateBytes is not null)
        {
            try
            {
                var st = EvidenceState.ParseStrict(stateBytes);
                if (st.State != expectedState.ToString())
                    problems.Add($"finalize:state: state must be exactly {expectedState}, found '{st.State}'");
                if (expectedState == EvidenceExpectedState.Complete)
                    ValidateCompleteSemantics(st, problems);
                if (st.RunId != identity.RunId || st.CandidateId != identity.CandidateId)
                    problems.Add("finalize:state: state identity does not match session identity");
            }
            catch (Exception ex)
            {
                problems.Add($"finalize:state: invalid run.state.json: {ex.Message}");
            }
        }

        // 6. strict run.json
        byte[]? runBytes = TryRead(problems, staging, EvidenceStagingSession.RunJsonName, "finalize:strict-validate");
        if (runBytes is not null)
        {
            try
            {
                runParsed = EvidenceJson.ReadRunJson(runBytes);
                ValidateRunIdentity(identity, runParsed, problems);
            }
            catch (Exception ex)
            {
                problems.Add($"finalize:strict-validate: invalid run.json: {ex.Message}");
            }
        }

        // 7. strict manifest + semantics
        byte[]? manifestOnDisk = TryRead(problems, staging, EvidenceStagingSession.ManifestName, "finalize:strict-validate");
        if (manifestOnDisk is not null)
        {
            try
            {
                manifestParsed = EvidenceJson.ReadManifest(manifestOnDisk);
                ValidateManifestIdentity(identity, manifestParsed, problems);
                ValidateManifestSemantics(session, manifestParsed, runBytes is null ? null : EvidenceControlWriter.Sha256(runBytes), problems);
                manifestBytes = manifestOnDisk;
                manifestSha = EvidenceControlWriter.Sha256(manifestOnDisk);
            }
            catch (Exception ex)
            {
                problems.Add($"finalize:strict-validate: invalid manifest: {ex.Message}");
            }
        }

        // 8. registered payloads current-disk facts + run.json manifest entry
        if (runBytes is not null && manifestParsed is not null)
        {
            try
            {
                string runSha = EvidenceControlWriter.Sha256(runBytes);
                var runEntry = manifestParsed.Artifacts.SingleOrDefault(a => a.RelativePath == EvidenceStagingSession.RunJsonName);
                if (runEntry is null)
                    problems.Add("finalize:strict-validate: manifest has no run.json entry");
                else if (runEntry.Bytes != runBytes.Length || runEntry.Sha256 != runSha)
                    problems.Add("finalize:strict-validate: run.json no longer matches its manifest entry");
                foreach (var e in session.RegisteredArtifacts)
                {
                    string full = EvidencePathSafety.ResolveUnderRoot(staging, e.RelativePath);
                    if (!File.Exists(full)) continue;
                    long len = new FileInfo(full).Length;
                    string sha = EvidenceControlWriter.Sha256(File.ReadAllBytes(full));
                    if (len != e.Bytes || sha != e.Sha256)
                        problems.Add($"finalize:strict-validate: registered payload '{e.RelativePath}' no longer matches snapshot");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"finalize:strict-validate: {ex.Message}");
            }
        }

        // 9. final destination absent
        if (File.Exists(final) || Directory.Exists(final))
            problems.Add($"finalize:final-destination: final destination already exists: {final}");

        bool isValid = problems.Count == 0;
        if (isValid && (runParsed is null || manifestParsed is null || manifestBytes is null || manifestSha is null))
        {
            isValid = false;
            problems.Add("finalize:strict-validate: valid readiness must expose run.json/manifest facts from current disk state");
        }
        return new EvidenceReadinessResult
        {
            IsValid = isValid,
            RunJson = runParsed,
            Manifest = manifestParsed,
            ManifestBytes = manifestBytes,
            ManifestSha256 = manifestSha,
            Problems = problems,
        };
    }

    private static void ValidateCompleteSemantics(EvidenceStateSnapshot st, List<string> problems)
    {
        if (st.Stage != "promote")
            problems.Add("finalize:state: Complete state must carry stage='promote'");
        if (st.Reason is not null)
            problems.Add("finalize:state: Complete state must not carry a reason");
        if (st.Utc is null)
            problems.Add("finalize:state: Complete state must carry a valid utc");
    }

    private static void CollectInventoryProblems(List<string> problems, EvidenceStagingSession session, TreeView tree, string[] controls)
    {
        var allowedFiles = session.RegisteredArtifacts.Select(e => e.RelativePath)
            .Append(EvidenceStagingSession.StateFileName).Concat(controls).ToHashSet(StringComparer.Ordinal);
        if (!tree.Files.ToHashSet(StringComparer.Ordinal).SetEquals(allowedFiles))
        {
            var unexpected = tree.Files.Where(f => !allowedFiles.Contains(f));
            var missing = allowedFiles.Where(f => !tree.Files.Contains(f));
            problems.Add($"finalize:inventory: unexpected files=[{string.Join(",", unexpected)}] missing=[{string.Join(",", missing)}]");
        }

        var allowedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in allowedFiles)
        {
            var segs = f.Split('/');
            for (int i = 1; i < segs.Length; i++)
                allowedDirs.Add(string.Join('/', segs.Take(i)));
        }
        var unexpectedDirs = tree.Directories.Where(d => !allowedDirs.Contains(d));
        if (unexpectedDirs.Any())
            problems.Add($"finalize:inventory: unexpected/empty directories=[{string.Join(",", unexpectedDirs)}]");
    }

    private static void ValidateRunIdentity(RunIdentity identity, EvidenceRunJson run, List<string> problems)
    {
        var mismatches = new List<string>();
        if (run.EvidenceSchemaVersion != EvidenceSchema.Version || identity.EvidenceSchemaVersion != EvidenceSchema.Version)
            mismatches.Add("evidence_schema_version");
        if (run.ProtocolVersion != identity.ProtocolVersion) mismatches.Add("protocol_version");
        if (run.CandidateId != identity.CandidateId) mismatches.Add("candidate_id");
        if (run.CandidateConfigId != identity.CandidateConfigId) mismatches.Add("candidate_config_id");
        if (run.WorkloadId != identity.WorkloadId) mismatches.Add("workload_id");
        if (run.CorpusId != identity.CorpusId) mismatches.Add("corpus_id");
        if (run.RunId != identity.RunId) mismatches.Add("run_id");
        if (mismatches.Count > 0)
            problems.Add("finalize:strict-validate: run.json identity mismatch: " + string.Join(", ", mismatches));
    }

    private static void ValidateManifestIdentity(RunIdentity identity, EvidenceManifest manifest, List<string> problems)
    {
        if (manifest.EvidenceSchemaVersion != EvidenceSchema.Version
            || manifest.CandidateId != identity.CandidateId
            || manifest.CandidateConfigId != identity.CandidateConfigId
            || manifest.WorkloadId != identity.WorkloadId
            || manifest.CorpusId != identity.CorpusId
            || manifest.RunId != identity.RunId)
            problems.Add("finalize:strict-validate: manifest identity does not match session identity");
    }

    private static void ValidateManifestSemantics(EvidenceStagingSession session, EvidenceManifest manifest, string? runSha, List<string> problems)
    {
        var registered = session.RegisteredArtifacts;
        var expected = registered.Select(e => e.RelativePath).Append(EvidenceStagingSession.RunJsonName).ToHashSet(StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        string? prev = null;
        int runJsonEntries = 0;
        foreach (var a in manifest.Artifacts)
        {
            if (!EvidencePathSafety.TryValidateArtifactPath(a.RelativePath, out _))
            {
                problems.Add($"finalize:strict-validate: invalid artifact path '{a.RelativePath}'");
                continue;
            }
            if (a.RelativePath == EvidenceStagingSession.StateFileName || a.RelativePath == EvidenceStagingSession.ManifestName)
                problems.Add($"finalize:strict-validate: reserved control entry '{a.RelativePath}'");
            if (a.RelativePath == EvidenceStagingSession.RunJsonName) runJsonEntries++;
            if (a.RelativePath == EvidenceStagingSession.RunJsonName && runSha is not null && (a.Bytes != -1 || a.Sha256 != runSha))
            {
                // bytes/hash cross-checked against fresh disk below; grammar first
            }
            if (!actual.Add(a.RelativePath)) problems.Add($"finalize:strict-validate: duplicate artifact '{a.RelativePath}'");
            if (prev is not null && string.CompareOrdinal(prev, a.RelativePath) >= 0)
                problems.Add("finalize:strict-validate: manifest artifacts not strictly ordinal-sorted");
            prev = a.RelativePath;
            if (a.Bytes < 0) problems.Add("finalize:strict-validate: negative artifact bytes");
            if (!EvidenceJson.IsValidSha256(a.Sha256))
                problems.Add($"finalize:strict-validate: malformed sha256 for '{a.RelativePath}'");
        }
        if (runJsonEntries != 1)
            problems.Add("finalize:strict-validate: manifest must contain exactly one run.json entry");
        if (!actual.SetEquals(expected))
            problems.Add("finalize:strict-validate: manifest artifact set does not equal registered + run.json");
        foreach (var e in registered)
        {
            var m = manifest.Artifacts.FirstOrDefault(x => x.RelativePath == e.RelativePath);
            if (m is null || m.Bytes != e.Bytes || m.Sha256 != e.Sha256)
                problems.Add($"finalize:strict-validate: registered snapshot mismatch for '{e.RelativePath}'");
        }
    }

    private static byte[]? TryRead(List<string> problems, string staging, string name, string category)
    {
        string path = Path.Combine(staging, name);
        if (!File.Exists(path))
        {
            problems.Add($"{category}: {name} missing");
            return null;
        }
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            problems.Add($"{category}: failed to read {name}: {ex.Message}");
            return null;
        }
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return EvidenceTreeInspector.IsSymlinkOrReparse(path);
        }
        catch
        {
            return true;
        }
    }
}

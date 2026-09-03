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
        foreach (var detail in EvidenceIntegrityChecks.CompleteStateProblems(st))
            problems.Add("finalize:state: " + detail);
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
        string mismatch = EvidenceIntegrityChecks.RunIdentityMismatch(identity, run);
        if (mismatch.Length > 0)
            problems.Add("finalize:strict-validate: " + mismatch);
    }

    private static void ValidateManifestIdentity(RunIdentity identity, EvidenceManifest manifest, List<string> problems)
    {
        string mismatch = EvidenceIntegrityChecks.ManifestIdentityMismatch(identity, manifest);
        if (mismatch.Length > 0)
            problems.Add("finalize:strict-validate: " + mismatch);
    }

    private static void ValidateManifestSemantics(EvidenceStagingSession session, EvidenceManifest manifest, string? runSha, List<string> problems)
    {
        _ = runSha; // run.json entry cross-checked against fresh disk later
        foreach (var detail in EvidenceIntegrityChecks.ManifestStructuralProblems(manifest))
            problems.Add("finalize:strict-validate: " + detail);

        var registered = session.RegisteredArtifacts;
        var expected = registered.Select(e => e.RelativePath).Append(EvidenceStagingSession.RunJsonName).ToHashSet(StringComparer.Ordinal);
        var actual = manifest.Artifacts.Select(a => a.RelativePath).ToHashSet(StringComparer.Ordinal);
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

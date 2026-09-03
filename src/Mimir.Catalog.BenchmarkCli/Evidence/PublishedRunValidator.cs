using System.Security.Cryptography;
using System.Text.Json;

namespace Mimir.Catalog.BenchmarkCli.Evidence;

public enum PublishedRunValidationStatus { Valid, Invalid, Error }

public sealed class PublishedRunValidationResult
{
    public required PublishedRunValidationStatus Status { get; init; }
    public required string FinalPath { get; init; }
    public EvidenceRunJson? RunJson { get; init; }
    public EvidenceManifest? Manifest { get; init; }
    public byte[]? ManifestBytes { get; init; }
    public string? ManifestSha256 { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
}

internal enum NodeKind { OrdinaryFile, OrdinaryDirectory, SymlinkOrReparse, Missing, InspectionError }

internal enum PublishedValidatorCheckpoint { AfterInitialTreeWalk, BeforeFinalConsistencyRecheck }

/// <summary>
/// Read-only published-run validator. Question asked independently of
/// promotion: does evidence at the authoritative final directory satisfy the
/// frozen structural/cryptographic contract? Manifest is the authoritative
/// inventory. Every control-file read and artifact hash is preceded by fresh
/// trusted-location + link/type inspection, so late mutations introduced after
/// the initial whole-tree walk are never silently followed.
/// </summary>
public static class PublishedRunValidator
{
    public static PublishedRunValidationResult Validate(string runsRoot, RunIdentity expectedIdentity)
        => ValidateCore(runsRoot, expectedIdentity, probe: null, checkpoint: null, enumerate: null);

    internal static PublishedRunValidationResult ValidateForTest(
        string runsRoot,
        RunIdentity expectedIdentity,
        Func<string, NodeKind>? probe = null,
        Action<PublishedValidatorCheckpoint>? checkpoint = null,
        Func<string, IEnumerable<string>>? enumerate = null)
        => ValidateCore(runsRoot, expectedIdentity, probe, checkpoint, enumerate);

    private static PublishedRunValidationResult ValidateCore(
        string runsRoot,
        RunIdentity expectedIdentity,
        Func<string, NodeKind>? probe,
        Action<PublishedValidatorCheckpoint>? checkpoint,
        Func<string, IEnumerable<string>>? enumerate)
    {
        NodeKind Inspect(string path) => probe?.Invoke(path) ?? InspectNode(path);
        var layout = RunLayoutPaths.Create(runsRoot, expectedIdentity.CandidateId, expectedIdentity.RunId);
        string final = layout.FinalPath;
        string candidateRoot = layout.CandidateRoot;
        var invalid = new List<string>();
        var errors = new List<string>();
        bool stopped = false;

        // 1. CandidateRoot / final-root location contract
        CheckTrustedLocation(candidateRoot, final, Inspect, invalid, errors);
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out var stoppedResult)) return stoppedResult;

        // 2. whole-tree walk (never follows links)
        WalkResult walk = WalkTree(final, probe, enumerate);
        if (walk.HadInspectionError) errors.Add("whole-tree inspection failed operationally");
        if (walk.LinkOrReparseFound) invalid.Add("final subtree contains a symlink/reparse point");
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;

        checkpoint?.Invoke(PublishedValidatorCheckpoint.AfterInitialTreeWalk);
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;

        // 3. safe control-file reads (each preceded by trusted-location + file-kind inspection)
        byte[]? stateBytes = ReadControlControl(candidateRoot, final, "run.state.json", Inspect, invalid, errors);
        byte[]? runBytes = ReadControlControl(candidateRoot, final, "run.json", Inspect, invalid, errors);
        byte[]? manifestBytes = ReadControlControl(candidateRoot, final, "evidence.manifest.json", Inspect, invalid, errors);
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;

        EvidenceStateSnapshot state = ParseState(stateBytes!, invalid, errors);
        EvidenceRunJson run = ParseRunJson(runBytes!, invalid, errors);
        EvidenceManifest manifest = ParseManifest(manifestBytes!, invalid, errors);
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;

        // 4. Complete semantics + identities (shared rules)
        if (state.State != "Complete") invalid.Add($"published state must be Complete, found '{state.State}'");
        if (state.RunId != expectedIdentity.RunId || state.CandidateId != expectedIdentity.CandidateId)
            invalid.Add("state identity does not match expected identity");
        foreach (var d in EvidenceIntegrityChecks.CompleteStateProblems(state)) invalid.Add(d);
        string runMismatch = EvidenceIntegrityChecks.RunIdentityMismatch(expectedIdentity, run);
        if (runMismatch.Length > 0) invalid.Add(runMismatch);
        string manifestMismatch = EvidenceIntegrityChecks.ManifestIdentityMismatch(expectedIdentity, manifest);
        if (manifestMismatch.Length > 0) invalid.Add(manifestMismatch);
        foreach (var d in EvidenceIntegrityChecks.ManifestStructuralProblems(manifest)) invalid.Add(d);
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;

        // 5. exact final inventory (manifest-derived authoritative sets)
        var expectedFiles = manifest.Artifacts.Select(a => a.RelativePath)
            .Append(EvidenceStagingSession.StateFileName)
            .Append(EvidenceStagingSession.ManifestName)
            .ToHashSet(StringComparer.Ordinal);
        var expectedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in expectedFiles)
        {
            var segs = f.Split('/');
            for (int i = 1; i < segs.Length; i++) expectedDirs.Add(string.Join('/', segs.Take(i)));
        }
        foreach (var missing in expectedFiles.Where(f => !walk.Files.Contains(f))) invalid.Add($"expected file missing: {missing}");
        foreach (var extra in walk.Files.Where(f => !expectedFiles.Contains(f))) invalid.Add($"unexpected file: {extra}");
        foreach (var d in walk.Directories.Where(d => !expectedDirs.Contains(d))) invalid.Add($"unexpected/empty directory: {d}");
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;

        // 6. per-artifact link-safe cryptographic validation (fresh trusted location first)
        foreach (var artifact in manifest.Artifacts)
        {
            if (artifact.RelativePath == EvidenceStagingSession.RunJsonName) continue; // safe-read exactly once as a control file
            CheckArtifact(candidateRoot, final, artifact, Inspect, invalid, errors);
            if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;
        }
        var runEntry = manifest.Artifacts.Single(a => a.RelativePath == EvidenceStagingSession.RunJsonName);
        if (runEntry.Bytes != runBytes!.Length || runEntry.Sha256 != Sha256(runBytes))
            invalid.Add("run.json no longer matches its manifest entry");
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;

        // 7. final consistency recheck: full tree against manifest-derived sets
        checkpoint?.Invoke(PublishedValidatorCheckpoint.BeforeFinalConsistencyRecheck);
        CheckTrustedLocation(candidateRoot, final, Inspect, invalid, errors);
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;

        WalkResult rewalk = WalkTree(final, probe, enumerate);
        if (rewalk.HadInspectionError) errors.Add("final consistency recheck failed operationally");
        if (rewalk.LinkOrReparseFound) invalid.Add("final subtree gained a symlink/reparse point during validation");
        foreach (var missing in expectedFiles.Where(f => !rewalk.Files.Contains(f))) invalid.Add($"file disappeared during validation: {missing}");
        foreach (var extra in rewalk.Files.Where(f => !expectedFiles.Contains(f))) invalid.Add($"file appeared during validation: {extra}");
        foreach (var d in rewalk.Directories.Where(d => !expectedDirs.Contains(d))) invalid.Add($"directory appeared/changed during validation: {d}");
        if (ReturnIfStop(layout, invalid, errors, ref stopped, out stoppedResult)) return stoppedResult;

        return new PublishedRunValidationResult
        {
            Status = PublishedRunValidationStatus.Valid,
            FinalPath = final,
            RunJson = run,
            Manifest = manifest,
            ManifestBytes = manifestBytes,
            ManifestSha256 = Sha256(manifestBytes!),
            Problems = Array.Empty<string>(),
        };
    }

    private static bool ReturnIfStop(RunLayoutPaths layout, List<string> invalid, List<string> errors,
        ref bool stopped, out PublishedRunValidationResult? result)
    {
        if (invalid.Count == 0 && errors.Count == 0)
        {
            result = null;
            return false;
        }
        stopped = true;
        result = new PublishedRunValidationResult
        {
            Status = errors.Count > 0 ? PublishedRunValidationStatus.Error
                : PublishedRunValidationStatus.Invalid,
            FinalPath = layout.FinalPath,
            Problems = invalid.Concat(errors).ToList(),
        };
        return true;
    }

    private static void CheckTrustedLocation(string candidateRoot, string final,
        Func<string, NodeKind> inspect, List<string> invalid, List<string> errors)
    {
        CheckDirectoryKind(candidateRoot, "candidate root", inspect, invalid, errors);
        CheckDirectoryKind(final, "final root", inspect, invalid, errors);
    }

    private static void CheckDirectoryKind(string path, string label,
        Func<string, NodeKind> inspect, List<string> invalid, List<string> errors)
    {
        NodeKind kind = inspect(path);
        switch (kind)
        {
            case NodeKind.OrdinaryDirectory: return;
            case NodeKind.SymlinkOrReparse: invalid.Add($"{label} is a symlink/reparse point"); break;
            case NodeKind.Missing: invalid.Add($"{label} missing"); break;
            case NodeKind.OrdinaryFile: invalid.Add($"{label} is a regular file, not a directory"); break;
            default: errors.Add($"failed to inspect {label}"); break;
        }
    }

    private static byte[]? ReadControlControl(string candidateRoot, string final, string name,
        Func<string, NodeKind> inspect, List<string> invalid, List<string> errors)
    {
        // trusted candidate/final, then exact top-level control target as an ordinary file
        CheckTrustedLocation(candidateRoot, final, inspect, invalid, errors);
        if (invalid.Count > 0 || errors.Count > 0) return null;
        string path = Path.Combine(final, name);
        NodeKind kind = inspect(path);
        switch (kind)
        {
            case NodeKind.OrdinaryFile: break;
            case NodeKind.OrdinaryDirectory: invalid.Add($"{name} is a directory, not a control file"); return null;
            case NodeKind.SymlinkOrReparse: invalid.Add($"{name} is a symlink/reparse point"); return null;
            case NodeKind.Missing: invalid.Add($"{name} missing"); return null;
            default: errors.Add($"cannot inspect {name}"); return null;
        }
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            errors.Add($"cannot read {name}: {ex.Message}");
            return null;
        }
    }

    private static EvidenceStateSnapshot ParseState(byte[] bytes, List<string> invalid, List<string> errors)
    {
        try { return EvidenceState.ParseStrict(bytes); }
        catch (JsonException ex) { invalid.Add("malformed run.state.json: " + ex.Message); return null!; }
        catch (Exception ex) { errors.Add("run.state.json parse error: " + ex.Message); return null!; }
    }

    private static EvidenceRunJson ParseRunJson(byte[] bytes, List<string> invalid, List<string> errors)
    {
        try { return EvidenceJson.ReadRunJson(bytes); }
        catch (JsonException ex) { invalid.Add("malformed run.json: " + ex.Message); return null!; }
        catch (Exception ex) { errors.Add("run.json parse error: " + ex.Message); return null!; }
    }

    private static EvidenceManifest ParseManifest(byte[] bytes, List<string> invalid, List<string> errors)
    {
        try { return EvidenceJson.ReadManifest(bytes); }
        catch (JsonException ex) { invalid.Add("malformed evidence.manifest.json: " + ex.Message); return null!; }
        catch (Exception ex) { errors.Add("manifest parse error: " + ex.Message); return null!; }
    }

    private static void CheckArtifact(string candidateRoot, string finalRoot, ManifestArtifact artifact,
        Func<string, NodeKind> inspect, List<string> invalid, List<string> errors)
    {
        // candidate root trusted again immediately before this read/hash
        CheckDirectoryKind(candidateRoot, "candidate root", inspect, invalid, errors);
        CheckDirectoryKind(finalRoot, "final root", inspect, invalid, errors);
        if (invalid.Count > 0 || errors.Count > 0) return;

        string full;
        try { full = EvidencePathSafety.ResolveUnderRoot(finalRoot, artifact.RelativePath); }
        catch (Exception ex) { invalid.Add($"artifact escapes final root: {artifact.RelativePath}: {ex.Message}"); return; }

        string? dir = Path.GetDirectoryName(full);
        while (dir is not null)
        {
            NodeKind kind = inspect(dir);
            if (kind == NodeKind.SymlinkOrReparse) { invalid.Add($"artifact parent is a symlink/reparse point: {artifact.RelativePath}"); return; }
            if (kind == NodeKind.Missing) { invalid.Add($"artifact parent missing: {artifact.RelativePath}"); return; }
            if (kind == NodeKind.OrdinaryFile) { invalid.Add($"artifact parent is a regular file: {artifact.RelativePath}"); return; }
            if (kind == NodeKind.InspectionError) { errors.Add($"cannot inspect artifact parent: {artifact.RelativePath}"); return; }
            if (EvidencePathSafety.IsSamePath(dir, finalRoot)) break;
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null || EvidencePathSafety.IsSamePath(parent, dir)) break;
            dir = parent;
        }

        NodeKind target = inspect(full);
        switch (target)
        {
            case NodeKind.OrdinaryFile: break;
            case NodeKind.OrdinaryDirectory: invalid.Add($"artifact replaced by directory: {artifact.RelativePath}"); return;
            case NodeKind.SymlinkOrReparse: invalid.Add($"artifact is a symlink/reparse point: {artifact.RelativePath}"); return;
            case NodeKind.Missing: invalid.Add($"artifact missing: {artifact.RelativePath}"); return;
            default: errors.Add($"cannot inspect artifact: {artifact.RelativePath}"); return;
        }

        byte[] content;
        try { content = File.ReadAllBytes(full); }
        catch (Exception ex) { errors.Add($"cannot read artifact '{artifact.RelativePath}': {ex.Message}"); return; }
        if (content.Length != artifact.Bytes) { invalid.Add($"artifact size mismatch: {artifact.RelativePath}"); return; }
        if (Sha256(content) != artifact.Sha256) { invalid.Add($"artifact hash mismatch: {artifact.RelativePath}"); return; }
    }

    private sealed class WalkResult
    {
        public List<string> Files { get; } = new();
        public List<string> Directories { get; } = new();
        public bool LinkOrReparseFound { get; set; }
        public bool HadInspectionError { get; set; }
    }

    private static WalkResult WalkTree(string root, Func<string, NodeKind>? probe, Func<string, IEnumerable<string>>? enumerate)
    {
        var result = new WalkResult();
        Walk(root, "", result, probe, enumerate);
        result.Files.Sort(StringComparer.Ordinal);
        result.Directories.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void Walk(string absolute, string relative, WalkResult result, Func<string, NodeKind>? probe, Func<string, IEnumerable<string>>? enumerate)
    {
        try
        {
            // EnumerateFileSystemEntries is lazy: iteration must stay inside the
            // try so failures while advancing the enumerator cannot escape.
            IEnumerable<string> entries = enumerate?.Invoke(absolute) ?? Directory.EnumerateFileSystemEntries(absolute);
            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                string rel = relative.Length == 0 ? name : relative + "/" + name;
                NodeKind kind = probe?.Invoke(entry) ?? InspectNode(entry);
                switch (kind)
                {
                    case NodeKind.SymlinkOrReparse:
                        result.LinkOrReparseFound = true;
                        break;
                    case NodeKind.InspectionError:
                        result.HadInspectionError = true;
                        break;
                    case NodeKind.OrdinaryDirectory:
                        result.Directories.Add(rel);
                        Walk(entry, rel, result, probe, enumerate);
                        break;
                    case NodeKind.OrdinaryFile:
                        result.Files.Add(rel);
                        break;
                    default:
                        result.HadInspectionError = true;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result.HadInspectionError = true;
        }
    }

    internal static NodeKind InspectNode(string path)
    {
        bool isDir;
        bool isFile;
        try
        {
            isDir = Directory.Exists(path);
            isFile = File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return NodeKind.InspectionError;
        }
        if (!isDir && !isFile) return NodeKind.Missing;
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
                return NodeKind.SymlinkOrReparse;
            var fs = isDir ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            if (fs.LinkTarget is not null || (fs.Attributes & FileAttributes.ReparsePoint) != 0)
                return NodeKind.SymlinkOrReparse;
            return isDir ? NodeKind.OrdinaryDirectory : NodeKind.OrdinaryFile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return NodeKind.InspectionError;
        }
    }

    internal static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(bytes));
    }
}

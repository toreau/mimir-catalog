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

internal enum NodeKind { Ordinary, SymlinkOrReparse, Missing, InspectionError }

/// <summary>
/// Read-only published-run validator. Question asked independently of
/// promotion: does evidence at the authoritative final directory satisfy the
/// frozen structural/cryptographic contract? Manifest is the authoritative
/// inventory. Never writes; never inspects a sibling .staging directory.
/// </summary>
public static class PublishedRunValidator
{
    public static PublishedRunValidationResult Validate(string runsRoot, RunIdentity expectedIdentity)
        => ValidateCore(runsRoot, expectedIdentity, probe: null);

    internal static PublishedRunValidationResult ValidateForTest(
        string runsRoot,
        RunIdentity expectedIdentity,
        Func<string, NodeKind> probe)
        => ValidateCore(runsRoot, expectedIdentity, probe);

    private static PublishedRunValidationResult ValidateCore(
        string runsRoot,
        RunIdentity expectedIdentity,
        Func<string, NodeKind>? probe)
    {
        NodeKind Inspect(string path) => probe?.Invoke(path) ?? InspectNode(path);
        var layout = RunLayoutPaths.Create(runsRoot, expectedIdentity.CandidateId, expectedIdentity.RunId);
        var final = layout.FinalPath;
        var invalid = new List<string>();
        var errors = new List<string>();

        // 1. CandidateRoot / final-root location contract
        NodeKind candidateKind = Inspect(layout.CandidateRoot);
        if (candidateKind == NodeKind.SymlinkOrReparse)
        { invalid.Add("candidate root is a symlink/reparse point"); return Result(layout, invalid, errors); }
        if (candidateKind == NodeKind.Missing)
        { invalid.Add("candidate root missing"); return Result(layout, invalid, errors); }
        if (candidateKind == NodeKind.InspectionError)
        { errors.Add("failed to inspect candidate root"); return Result(layout, invalid, errors); }

        NodeKind finalKind = Inspect(final);
        if (finalKind == NodeKind.SymlinkOrReparse)
        { invalid.Add("final root is a symlink/reparse point"); return Result(layout, invalid, errors); }
        if (finalKind == NodeKind.Missing)
        { invalid.Add("final run directory missing"); return Result(layout, invalid, errors); }
        if (finalKind == NodeKind.InspectionError)
        { errors.Add("failed to inspect final run directory"); return Result(layout, invalid, errors); }

        // 2. whole-tree walk (never follows links)
        WalkResult walk = WalkTree(final, probe);
        if (walk.HadInspectionError) { errors.Add("whole-tree inspection failed operationally"); }
        if (walk.LinkOrReparseFound) { invalid.Add("final subtree contains a symlink/reparse point"); }
        if (errors.Count > 0) return Result(layout, invalid, errors);
        if (invalid.Count > 0) return Result(layout, invalid, errors);

        // 3. strict control files (read once, parse strictly)
        byte[]? stateBytes = ReadBytes(final, "run.state.json", invalid, errors);
        byte[]? runBytes = ReadBytes(final, "run.json", invalid, errors);
        byte[]? manifestBytes = ReadBytes(final, "evidence.manifest.json", invalid, errors);
        if (errors.Count > 0 || invalid.Count > 0) return Result(layout, invalid, errors);

        EvidenceStateSnapshot state = ParseState(stateBytes!, invalid, errors);
        EvidenceRunJson run = ParseRunJson(runBytes!, invalid, errors);
        EvidenceManifest manifest = ParseManifest(manifestBytes!, invalid, errors);
        if (errors.Count > 0) return Result(layout, invalid, errors);
        if (invalid.Count > 0) return Result(layout, invalid, errors);

        // 4. Complete semantics + identities (shared rules)
        if (state.State != "Complete")
            invalid.Add($"published state must be Complete, found '{state.State}'");
        if (state.RunId != expectedIdentity.RunId || state.CandidateId != expectedIdentity.CandidateId)
            invalid.Add("state identity does not match expected identity");
        foreach (var d in EvidenceIntegrityChecks.CompleteStateProblems(state)) invalid.Add(d);
        string runMismatch = EvidenceIntegrityChecks.RunIdentityMismatch(expectedIdentity, run);
        if (runMismatch.Length > 0) invalid.Add(runMismatch);
        string manifestMismatch = EvidenceIntegrityChecks.ManifestIdentityMismatch(expectedIdentity, manifest);
        if (manifestMismatch.Length > 0) invalid.Add(manifestMismatch);
        foreach (var d in EvidenceIntegrityChecks.ManifestStructuralProblems(manifest)) invalid.Add(d);
        if (invalid.Count > 0) return Result(layout, invalid, errors);

        // 5. exact final inventory (manifest artifacts + state + manifest)
        var expectedFiles = manifest.Artifacts.Select(a => a.RelativePath)
            .Append(EvidenceStagingSession.StateFileName)
            .Append(EvidenceStagingSession.ManifestName)
            .ToHashSet(StringComparer.Ordinal);
        var actualFiles = walk.Files.ToHashSet(StringComparer.Ordinal);
        foreach (var missing in expectedFiles.Where(f => !actualFiles.Contains(f))) invalid.Add($"expected file missing: {missing}");
        foreach (var extra in actualFiles.Where(f => !expectedFiles.Contains(f))) invalid.Add($"unexpected file: {extra}");
        var expectedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in expectedFiles)
        {
            var segs = f.Split('/');
            for (int i = 1; i < segs.Length; i++) expectedDirs.Add(string.Join('/', segs.Take(i)));
        }
        foreach (var d in walk.Directories.Where(d => !expectedDirs.Contains(d))) invalid.Add($"unexpected/empty directory: {d}");
        if (invalid.Count > 0) return Result(layout, invalid, errors);

        // 6. per-artifact link-safe cryptographic validation
        foreach (var artifact in manifest.Artifacts)
        {
            CheckArtifact(final, artifact, invalid, errors, probe);
            if (errors.Count > 0) return Result(layout, invalid, errors);
        }

        // run.json entry must match the already-read final bytes
        var runEntry = manifest.Artifacts.Single(a => a.RelativePath == EvidenceStagingSession.RunJsonName);
        string runSha = Sha256(runBytes!);
        if (runEntry.Bytes != runBytes!.Length || runEntry.Sha256 != runSha)
            invalid.Add("run.json no longer matches its manifest entry");
        if (invalid.Count > 0) return Result(layout, invalid, errors);

        // 7. final consistency recheck (location + inventory only, no evidence reread)
        if (Inspect(layout.CandidateRoot) != NodeKind.Ordinary) invalid.Add("candidate root changed during validation");
        if (Inspect(final) != NodeKind.Ordinary) invalid.Add("final root changed during validation");
        WalkResult rewalk = WalkTree(final, probe);
        if (rewalk.HadInspectionError) errors.Add("final consistency recheck failed operationally");
        if (!rewalk.Files.ToHashSet(StringComparer.Ordinal).SetEquals(actualFiles)) invalid.Add("final file inventory changed during validation");
        if (invalid.Count > 0) return Result(layout, invalid, errors);
        if (errors.Count > 0) return Result(layout, invalid, errors);

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

    private static PublishedRunValidationResult Result(RunLayoutPaths layout, List<string> invalid, List<string> errors)
    {
        PublishedRunValidationStatus status = errors.Count > 0
            ? PublishedRunValidationStatus.Error
            : invalid.Count > 0
                ? PublishedRunValidationStatus.Invalid
                : PublishedRunValidationStatus.Valid;
        return new PublishedRunValidationResult
        {
            Status = status,
            FinalPath = layout.FinalPath,
            Problems = invalid.Concat(errors).ToList(),
        };
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

    private static void CheckArtifact(string finalRoot, ManifestArtifact artifact, List<string> invalid, List<string> errors, Func<string, NodeKind>? probe)
    {
        NodeKind Inspect(string path) => probe?.Invoke(path) ?? InspectNode(path);
        string full;
        try { full = EvidencePathSafety.ResolveUnderRoot(finalRoot, artifact.RelativePath); }
        catch (Exception ex) { invalid.Add($"artifact escapes final root: {artifact.RelativePath}: {ex.Message}"); return; }

        // parent chain ordinary before touching the file
        string? dir = Path.GetDirectoryName(full);
        while (dir is not null)
        {
            NodeKind kind = Inspect(dir);
            if (kind == NodeKind.SymlinkOrReparse) { invalid.Add($"artifact parent is a symlink/reparse point: {artifact.RelativePath}"); return; }
            if (kind == NodeKind.Missing) { invalid.Add($"artifact parent missing: {artifact.RelativePath}"); return; }
            if (kind == NodeKind.InspectionError) { errors.Add($"cannot inspect artifact parent: {artifact.RelativePath}"); return; }
            if (EvidencePathSafety.IsSamePath(dir, finalRoot)) break;
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null || EvidencePathSafety.IsSamePath(parent, dir)) break;
            dir = parent;
        }

        NodeKind targetKind = Inspect(full);
        if (targetKind == NodeKind.SymlinkOrReparse) { invalid.Add($"artifact is a symlink/reparse point: {artifact.RelativePath}"); return; }
        if (targetKind == NodeKind.Missing) { invalid.Add($"artifact missing: {artifact.RelativePath}"); return; }
        if (targetKind == NodeKind.InspectionError) { errors.Add($"cannot inspect artifact: {artifact.RelativePath}"); return; }

        byte[] content;
        try { content = File.ReadAllBytes(full); }
        catch (Exception ex) { errors.Add($"cannot read artifact '{artifact.RelativePath}': {ex.Message}"); return; }
        if (content.Length != artifact.Bytes) { invalid.Add($"artifact size mismatch: {artifact.RelativePath}"); return; }
        if (Sha256(content) != artifact.Sha256) { invalid.Add($"artifact hash mismatch: {artifact.RelativePath}"); return; }
    }

    private static byte[]? ReadBytes(string root, string name, List<string> invalid, List<string> errors)
    {
        string path = Path.Combine(root, name);
        if (!File.Exists(path)) { invalid.Add($"{name} missing"); return null; }
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) { errors.Add($"cannot read {name}: {ex.Message}"); return null; }
    }

    private sealed class WalkResult
    {
        public List<string> Files { get; } = new();
        public List<string> Directories { get; } = new();
        public bool LinkOrReparseFound { get; set; }
        public bool HadInspectionError { get; set; }
    }

    private static WalkResult WalkTree(string root, Func<string, NodeKind>? probe)
    {
        var result = new WalkResult();
        Walk(root, "", result, probe);
        result.Files.Sort(StringComparer.Ordinal);
        result.Directories.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void Walk(string absolute, string relative, WalkResult result, Func<string, NodeKind>? probe)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(absolute); }
        catch (Exception ex) { result.HadInspectionError = true; result.Files.Add($"__io__{relative}"); return; }

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
                case NodeKind.Ordinary:
                    if (Directory.Exists(entry)) { result.Directories.Add(rel); Walk(entry, rel, result, probe); }
                    else { result.Files.Add(rel); }
                    break;
                default:
                    result.HadInspectionError = true;
                    break;
            }
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
            return NodeKind.Ordinary;
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

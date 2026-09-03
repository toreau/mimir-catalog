using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mimir.Catalog.BenchmarkCli.Evidence;

/// <summary>
/// Exclusive staging session for one benchmark run (1c.1). Creates the staging
/// directory, writes Running state, and supports write-new/register-existing
/// artifacts with SHA-256 + byte inventory. No publication, no Complete state,
/// no manifest.
/// </summary>
public sealed class EvidenceStagingSession : IDisposable
{
    public const string StateFileName = "run.state.json";
    public const string RunJsonName = "run.json";
    public const string ManifestName = "evidence.manifest.json";
    private const string TempStatePrefix = ".state-tmp-";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly HashSet<string> ReservedTopLevel = new(StringComparer.Ordinal)
    {
        StateFileName, RunJsonName, ManifestName,
    };

    private readonly List<EvidenceArtifactEntry> _inventory = new();

    public RunIdentity Identity { get; }
    public RunLayoutPaths Layout { get; }
    public string StagingPath => Layout.StagingPath;
    public string FinalPath => Layout.FinalPath;

    private EvidenceStagingSession(RunIdentity identity, RunLayoutPaths layout)
    {
        Identity = identity;
        Layout = layout;
    }

    /// <summary>Creates a staging session: validate, layout, refuse collisions, create, write Running.</summary>
    public static EvidenceStagingSession Create(string runsRoot, RunIdentity identity)
    {
        var identityErrors = identity.Validate();
        if (identityErrors.Count > 0)
            throw new EvidenceStagingException("invalid run identity: " + string.Join("; ", identityErrors));

        var layout = RunLayoutPaths.Create(runsRoot, identity.CandidateId, identity.RunId);
        if (Directory.Exists(layout.FinalPath))
            throw new EvidenceStagingException($"final path already exists and is never deleted/reused: {layout.FinalPath}");
        if (Directory.Exists(layout.StagingPath))
            throw new EvidenceStagingException($"staging path already exists and is never reused: {layout.StagingPath}");

        Directory.CreateDirectory(layout.CandidateRoot);
        try
        {
            Directory.CreateDirectory(layout.StagingPath);
        }
        catch (Exception ex)
        {
            throw new EvidenceStagingException($"failed to create staging directory: {ex.Message}", ex);
        }

        var session = new EvidenceStagingSession(identity, layout);
        try
        {
            WriteStateAtomic(layout.StagingPath, new RunEvidenceState
            {
                State = "Running",
                RunId = identity.RunId,
                CandidateId = identity.CandidateId,
                Stage = "create",
                Utc = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            // Retain staging; Failed state is best-effort, never silently cleaned.
            try
            {
                WriteStateAtomic(layout.StagingPath, new RunEvidenceState
                {
                    State = "Failed",
                    RunId = identity.RunId,
                    CandidateId = identity.CandidateId,
                    Stage = "create",
                    Reason = "failed to write Running state: " + ex.Message,
                    Utc = DateTime.UtcNow,
                });
            }
            catch
            {
                // best-effort
            }
            throw new EvidenceStagingException("failed to write Running state", ex);
        }
        return session;
    }

    /// <summary>Write-new artifact (never overwrites, never duplicates).</summary>
    public EvidenceArtifactEntry WriteBytes(string relativePath, byte[] bytes)
    {
        ValidateWritable(relativePath);
        string full = EvidencePathSafety.ResolveUnderRoot(StagingPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
            fs.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is not EvidenceStagingException)
        {
            try { File.Delete(full); } catch { }
            throw new EvidenceStagingException($"failed to write artifact '{relativePath}': {ex.Message}", ex);
        }
        return AddSnapshot(relativePath, full);
    }

    public EvidenceArtifactEntry WriteText(string relativePath, string text)
        => WriteBytes(relativePath, new UTF8Encoding(false).GetBytes(text));

    public EvidenceArtifactEntry WriteJson(string relativePath, string json)
        => WriteText(relativePath, json);

    /// <summary>Register an already-produced ordinary file (e.g. future /usr/bin/time -o output).</summary>
    public EvidenceArtifactEntry RegisterExisting(string relativePath)
    {
        ValidateWritable(relativePath);
        string full = EvidencePathSafety.ResolveUnderRoot(StagingPath, relativePath);
        AssertOrdinaryFile(full, relativePath);
        return AddSnapshot(relativePath, full);
    }

    public IReadOnlyList<EvidenceArtifactEntry> RegisteredArtifacts
        => _inventory.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToList();

    /// <summary>Re-stats every registered artifact; any mutation is reported.</summary>
    public IReadOnlyList<string> VerifyRegisteredArtifacts()
    {
        var problems = new List<string>();
        foreach (var entry in RegisteredArtifacts)
        {
            string full = EvidencePathSafety.ResolveUnderRoot(StagingPath, entry.RelativePath);
            if (!File.Exists(full))
            {
                problems.Add($"{entry.RelativePath}: missing");
                continue;
            }
            if (IsReparseOrDirectory(full))
            {
                problems.Add($"{entry.RelativePath}: replaced by directory/symlink/reparse point");
                continue;
            }
            if (new FileInfo(full).Length != entry.Bytes)
            {
                problems.Add($"{entry.RelativePath}: size changed");
                continue;
            }
            string sha = Sha256Of(full);
            if (!string.Equals(sha, entry.Sha256, StringComparison.Ordinal))
                problems.Add($"{entry.RelativePath}: content changed");
        }
        return problems;
    }

    /// <summary>Enumerates files under staging that are neither registered artifacts nor known control files.</summary>
    public IReadOnlyList<string> FindUnexpectedFiles()
    {
        var unexpected = new List<string>();
        foreach (string full in Directory.EnumerateFiles(StagingPath, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(StagingPath, full).Replace(Path.DirectorySeparatorChar, '/');
            if (IsKnownControl(rel)) continue;
            if (_inventory.Any(e => e.RelativePath == rel)) continue;
            unexpected.Add(rel);
        }
        return unexpected.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>Best-effort Failed state; staging and all diagnostic files are retained.</summary>
    public IReadOnlyList<string> Fail(string stage, string reason)
    {
        var warnings = new List<string>();
        try
        {
            WriteStateAtomic(StagingPath, new RunEvidenceState
            {
                State = "Failed",
                RunId = Identity.RunId,
                CandidateId = Identity.CandidateId,
                Stage = stage,
                Reason = reason,
                Utc = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            warnings.Add($"failed to write Failed state: {ex.Message}");
        }
        return warnings;
    }

    public static bool IsReservedControlPath(string canonicalRelative)
        => IsKnownControl(canonicalRelative);

    /// <summary>No-op: the session never owns the staging tree's lifecycle for 1c.1.</summary>
    public void Dispose() { }

    private static bool IsKnownControl(string canonicalRelative)
    {
        int slash = canonicalRelative.IndexOf('/');
        string first = slash < 0 ? canonicalRelative : canonicalRelative[..slash];
        if (ReservedTopLevel.Contains(first)) return true;
        return first.StartsWith(TempStatePrefix, StringComparison.Ordinal);
    }

    private static bool IsInternalTempFile(string canonicalRelative)
    {
        int slash = canonicalRelative.IndexOf('/');
        string first = slash < 0 ? canonicalRelative : canonicalRelative[..slash];
        return first.StartsWith(TempStatePrefix, StringComparison.Ordinal);
    }

    private void ValidateWritable(string relativePath)
    {
        if (!EvidencePathSafety.TryValidateArtifactPath(relativePath, out string? error))
            throw new EvidenceStagingException(error ?? "invalid artifact relative path");
        if (IsKnownControl(relativePath) || IsInternalTempFile(relativePath))
            throw new EvidenceStagingException($"artifact path is reserved for evidence control: {relativePath}");
        if (_inventory.Any(e => e.RelativePath == relativePath))
            throw new EvidenceStagingException($"artifact already registered: {relativePath}");
    }

    private EvidenceArtifactEntry AddSnapshot(string relativePath, string full)
    {
        AssertOrdinaryFile(full, relativePath);
        long bytes = new FileInfo(full).Length;
        string sha = Sha256Of(full);
        var entry = new EvidenceArtifactEntry(relativePath, bytes, sha);
        _inventory.Add(entry);
        return entry;
    }

    private void AssertOrdinaryFile(string full, string relativePath)
    {
        if (!File.Exists(full))
            throw new EvidenceStagingException($"file does not exist: {relativePath}");
        if (IsReparseOrDirectory(full))
            throw new EvidenceStagingException($"artifact is not an ordinary file (symlink/reparse/directory): {relativePath}");
        // Parent chain within staging must not traverse reparse points.
        string? dir = Path.GetDirectoryName(full);
        string staging = Path.GetFullPath(StagingPath);
        while (dir is not null)
        {
            if (new DirectoryInfo(dir).LinkTarget is not null || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new EvidenceStagingException($"artifact parent traverses a reparse point/symlink: {relativePath}");
            if (string.Equals(Path.GetFullPath(dir), staging, StringComparison.Ordinal)) break;
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static bool IsReparseOrDirectory(string full)
    {
        if (Directory.Exists(full)) return true;
        var fi = new FileInfo(full);
        if (fi.LinkTarget is not null) return true;
        return (fi.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static string Sha256Of(string full)
    {
        using var fs = File.OpenRead(full);
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(fs));
    }

    private static void WriteStateAtomic(string staging, RunEvidenceState state)
    {
        string target = Path.Combine(staging, StateFileName);
        string tmp = Path.Combine(staging, TempStatePrefix + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

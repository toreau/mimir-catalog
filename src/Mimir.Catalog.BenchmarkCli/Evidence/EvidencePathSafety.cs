using System.Text.RegularExpressions;

namespace Mimir.Catalog.BenchmarkCli.Evidence;

/// <summary>
/// Portable safe-component and artifact relative-path rules. Artifact relative
/// paths are canonical with '/' separators; native mapping splits on '/'.
/// Native filesystem equality is OS-appropriate (Ordinal on Unix, OrdinalIgnoreCase on Windows).
/// </summary>
public static partial class EvidencePathSafety
{
    /// <summary>^[A-Za-z0-9][A-Za-z0-9._-]*$ — no leading dot, no separators.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeComponentRegex();

    public static StringComparison NativeComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsValidComponent(string component)
        => !string.IsNullOrEmpty(component) && SafeComponentRegex().IsMatch(component);

    /// <summary>
    /// Validates a canonical evidence relative path: '/'-separated, non-rooted,
    /// no '\', no empty/dot/dotdot segments, each segment a safe component.
    /// </summary>
    public static bool TryValidateArtifactPath(string relativePath, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(relativePath))
        {
            error = "artifact relative path must be non-empty";
            return false;
        }
        if (relativePath.StartsWith('/'))
        {
            error = "artifact relative path must not be rooted";
            return false;
        }
        if (relativePath.EndsWith('/'))
        {
            error = "artifact relative path must not end with '/'";
            return false;
        }
        if (relativePath.Contains('\\'))
        {
            error = "artifact relative path must not contain '\\'";
            return false;
        }
        if (relativePath.Contains(':'))
        {
            error = "artifact relative path must not contain ':'";
            return false;
        }
        string[] segments = relativePath.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                error = "artifact relative path contains an empty segment";
                return false;
            }
            if (segment is "." or "..")
            {
                error = $"artifact relative path must not contain '{segment}' segments";
                return false;
            }
            if (!IsValidComponent(segment))
            {
                error = $"artifact segment '{segment}' violates the safe component rule";
                return false;
            }
        }
        return true;
    }

    public static bool IsSamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), NativeComparison);

    /// <summary>Resolves a canonical relative path beneath a root with a real path-boundary check.</summary>
    public static string ResolveUnderRoot(string root, string canonicalRelative)
    {
        string rootFull = Path.GetFullPath(root);
        string combined = Path.Combine(new[] { rootFull }.Concat(canonicalRelative.Split('/')).ToArray());
        string full = Path.GetFullPath(combined);
        string boundary = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundary, NativeComparison))
            throw new EvidenceStagingException($"artifact path escapes staging root: {canonicalRelative}");
        return full;
    }
}

/// <summary>Derived same-parent staging/final layout for one run.</summary>
public sealed record RunLayoutPaths(string RunsRoot, string CandidateRoot, string FinalPath, string StagingPath)
{
    public static RunLayoutPaths Create(string runsRoot, string candidateId, string runId)
    {
        if (string.IsNullOrWhiteSpace(runsRoot))
            throw new EvidenceStagingException("runsRoot must be non-empty");
        if (!EvidencePathSafety.IsValidComponent(candidateId))
            throw new EvidenceStagingException($"candidateId '{candidateId}' violates the safe component rule");
        if (!EvidencePathSafety.IsValidComponent(runId))
            throw new EvidenceStagingException($"runId '{runId}' violates the safe component rule");
        string candidateRoot = Path.Combine(runsRoot, candidateId);
        return new RunLayoutPaths(
            RunsRoot: runsRoot,
            CandidateRoot: candidateRoot,
            FinalPath: Path.Combine(candidateRoot, runId),
            StagingPath: Path.Combine(candidateRoot, runId + ".staging"));
    }
}

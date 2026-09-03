namespace Mimir.Catalog.BenchmarkCli.Evidence;

/// <summary>Deterministic manifest builder (identity header + registered artifacts + run.json).</summary>
public static class EvidenceManifestBuilder
{
    public static EvidenceManifest Build(RunIdentity identity, IReadOnlyList<EvidenceArtifactEntry> registered, ManifestArtifact runJsonEntry)
    {
        var artifacts = registered
            .Select(e => new ManifestArtifact(e.RelativePath, e.Bytes, e.Sha256))
            .Append(runJsonEntry)
            .OrderBy(a => a.RelativePath, StringComparer.Ordinal)
            .ToList();
        return new EvidenceManifest(
            identity.EvidenceSchemaVersion,
            identity.CandidateId,
            identity.CandidateConfigId,
            identity.WorkloadId,
            identity.CorpusId,
            identity.RunId,
            artifacts);
    }
}

/// <summary>
/// Evidence-owned immutable control-file writer. Create-new only; existing files
/// block and are preserved; a partial file is cleaned up only when this exact
/// call created it.
/// </summary>
internal static class EvidenceControlWriter
{
    public static void WriteCreateNew(string stagingRoot, string fileName, byte[] bytes)
    {
        string full = Path.Combine(stagingRoot, fileName);
        if (File.Exists(full) || Directory.Exists(full))
            throw new EvidenceStagingException($"control file already exists and is preserved: {fileName}");
        bool created = false;
        try
        {
            using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
            created = true;
            fs.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is not EvidenceStagingException)
        {
            if (created)
            {
                try { File.Delete(full); } catch { }
            }
            throw new EvidenceStagingException($"failed to write control file '{fileName}': {ex.Message}", ex);
        }
    }

    public static string Sha256(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(bytes));
    }
}

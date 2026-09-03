namespace Mimir.Catalog.BenchmarkCli.Evidence;

/// <summary>
/// Shared, session-independent evidence contract checks used by BOTH the
/// pre-publication readiness validator and the post-publication published-run
/// validator, so their rules for identity, manifest structure/grammar and
/// Complete semantics cannot diverge. Pure detail strings; callers own their
/// category prefixes/classification.
/// </summary>
internal static class EvidenceIntegrityChecks
{
    public static string RunIdentityMismatch(RunIdentity expected, EvidenceRunJson run)
    {
        var mismatches = new List<string>();
        if (run.EvidenceSchemaVersion != EvidenceSchema.Version || expected.EvidenceSchemaVersion != EvidenceSchema.Version)
            mismatches.Add("evidence_schema_version");
        if (run.ProtocolVersion != expected.ProtocolVersion) mismatches.Add("protocol_version");
        if (run.CandidateId != expected.CandidateId) mismatches.Add("candidate_id");
        if (run.CandidateConfigId != expected.CandidateConfigId) mismatches.Add("candidate_config_id");
        if (run.WorkloadId != expected.WorkloadId) mismatches.Add("workload_id");
        if (run.CorpusId != expected.CorpusId) mismatches.Add("corpus_id");
        if (run.RunId != expected.RunId) mismatches.Add("run_id");
        return mismatches.Count == 0 ? "" : "run.json identity mismatch: " + string.Join(", ", mismatches);
    }

    public static string ManifestIdentityMismatch(RunIdentity expected, EvidenceManifest manifest)
    {
        if (manifest.EvidenceSchemaVersion != EvidenceSchema.Version
            || manifest.CandidateId != expected.CandidateId
            || manifest.CandidateConfigId != expected.CandidateConfigId
            || manifest.WorkloadId != expected.WorkloadId
            || manifest.CorpusId != expected.CorpusId
            || manifest.RunId != expected.RunId)
            return "manifest identity does not match session identity";
        return "";
    }

    public static IReadOnlyList<string> CompleteStateProblems(EvidenceStateSnapshot st)
    {
        var problems = new List<string>();
        if (st.Stage != "promote") problems.Add("Complete state must carry stage='promote'");
        if (st.Reason is not null) problems.Add("Complete state must not carry a reason");
        if (st.Utc is null) problems.Add("Complete state must carry a valid utc");
        return problems;
    }

    /// <summary>
    /// Pure manifest structural/grammar contract (no session knowledge):
    /// canonical safe paths, strict ordinal ordering, no duplicates, bytes&gt;=0,
    /// lowercase 64-hex SHA, exactly one run.json, no state/manifest entries.
    /// </summary>
    public static IReadOnlyList<string> ManifestStructuralProblems(EvidenceManifest manifest)
    {
        var problems = new List<string>();
        var actual = new HashSet<string>(StringComparer.Ordinal);
        string? prev = null;
        int runJsonEntries = 0;
        foreach (var a in manifest.Artifacts)
        {
            if (!EvidencePathSafety.TryValidateArtifactPath(a.RelativePath, out _))
            {
                problems.Add($"invalid artifact path '{a.RelativePath}'");
                continue;
            }
            if (a.RelativePath == EvidenceStagingSession.StateFileName || a.RelativePath == EvidenceStagingSession.ManifestName)
                problems.Add($"reserved control entry '{a.RelativePath}'");
            if (a.RelativePath == EvidenceStagingSession.RunJsonName) runJsonEntries++;
            if (!actual.Add(a.RelativePath)) problems.Add($"duplicate artifact '{a.RelativePath}'");
            if (prev is not null && string.CompareOrdinal(prev, a.RelativePath) >= 0)
                problems.Add("manifest artifacts not strictly ordinal-sorted");
            prev = a.RelativePath;
            if (a.Bytes < 0) problems.Add("negative artifact bytes");
            if (!EvidenceJson.IsValidSha256(a.Sha256))
                problems.Add($"malformed sha256 for '{a.RelativePath}'");
        }
        if (runJsonEntries != 1)
            problems.Add("manifest must contain exactly one run.json entry");
        return problems;
    }
}

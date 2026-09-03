namespace Mimir.Catalog.BenchmarkCli.Evidence;

/// <summary>Frozen evidence schema identifier (v1).</summary>
public static class EvidenceSchema
{
    public const string Version = "mimir-catalog-benchmark-evidence-v1";
}

/// <summary>
/// Immutable run-level identity. Operational correlation only; never benchmark
/// content identity. Timestamps/PID/hostname/paths are excluded.
/// </summary>
public sealed class RunIdentity
{
    public required string EvidenceSchemaVersion { get; set; }
    public required string ProtocolVersion { get; set; }
    public required string CandidateId { get; set; }
    public required string CandidateConfigId { get; set; }
    public required string WorkloadId { get; set; }
    public required string CorpusId { get; set; }
    public required string RunId { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        Require(EvidenceSchemaVersion, nameof(EvidenceSchemaVersion), errors);
        Require(ProtocolVersion, nameof(ProtocolVersion), errors);
        Require(CandidateId, nameof(CandidateId), errors);
        Require(CandidateConfigId, nameof(CandidateConfigId), errors);
        Require(WorkloadId, nameof(WorkloadId), errors);
        Require(CorpusId, nameof(CorpusId), errors);
        Require(RunId, nameof(RunId), errors);
        return errors;
    }

    private static void Require(string value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{name} must be non-empty");
    }
}

/// <summary>One registered artifact integrity snapshot.</summary>
public sealed record EvidenceArtifactEntry(string RelativePath, long Bytes, string Sha256);

/// <summary>Operational run state persisted to run.state.json.</summary>
public sealed class RunEvidenceState
{
    public required string State { get; set; }
    public required string RunId { get; set; }
    public required string CandidateId { get; set; }
    public string? Stage { get; set; }
    public string? Reason { get; set; }
    public DateTime? Utc { get; set; }
}

public sealed class EvidenceStagingException : Exception
{
    public EvidenceStagingException(string message) : base(message) { }
    public EvidenceStagingException(string message, Exception inner) : base(message, inner) { }
}

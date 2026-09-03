using Mimir.Catalog.Storage.Sqlite;

namespace Mimir.Catalog.BenchmarkCli.Protocol;

/// <summary>
/// Authoritative Candidate A composition identities. Reuses the frozen public
/// constants from SqliteCandidatePreflight; candidateId is the single
/// composition constant owned by the CLI (no prior single source of truth).
/// </summary>
public static class CandidateAIdentity
{
    public const string CandidateId = "sqlite-native-v1";
    public const string CandidateConfigId = SqliteCandidatePreflight.ExpectedConfigId;
    public const string WorkloadId = SqliteCandidatePreflight.OfficialWorkloadId;
    public const string CorpusId = SqliteCandidatePreflight.OfficialCorpusId;
    public const string ManifestSha = SqliteCandidatePreflight.ExpectedManifestSha;
}

/// <summary>One authoritative protocol identifier; exact ordinal equality required.</summary>
public static class ProtocolConstants
{
    public const string ChildProtocolVersion = "mimir-catalog-benchmark-child-v1";
}

/// <summary>Closed workload-class domain for future 4d orchestration.</summary>
public enum WorkloadClass
{
    Serving,
    G1,
    G2,
    Analytical,
    Open,
    Build,
}

/// <summary>
/// Logical benchmark outcome carried inside a result envelope. A child never
/// emits an authoritative TIMEOUT; external timeout ownership belongs to the
/// parent process runner (4d.1b).
/// </summary>
public enum LogicalStatus
{
    Valid,
    Invalid,
    Error,
}

public sealed class ChildRequestEnvelope
{
    public required string ProtocolVersion { get; set; }
    public required string CandidateId { get; set; }
    public required string CandidateConfigId { get; set; }
    public required string WorkloadId { get; set; }
    public required string CorpusId { get; set; }
    public required WorkloadClass WorkloadClass { get; set; }
    public required string Operation { get; set; }
    public required int Repetition { get; set; }
    public required string CandidatePath { get; set; }
    public required string WorkloadPath { get; set; }
    public required string RunId { get; set; }
}

public sealed class ChildResultEnvelope
{
    public required string ProtocolVersion { get; set; }
    public required string CandidateId { get; set; }
    public required string CandidateConfigId { get; set; }
    public required string WorkloadId { get; set; }
    public required string CorpusId { get; set; }
    public required WorkloadClass WorkloadClass { get; set; }
    public required string Operation { get; set; }
    public required int Repetition { get; set; }
    public required LogicalStatus Status { get; set; }
    public required string CorrectnessStatus { get; set; }
    public double? WallSeconds { get; set; }
    public long? ResultCardinality { get; set; }
    public string? ResultDigest { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Exit codes: 0 = one valid result envelope was emitted; nonzero = no trustworthy result.</summary>
public static class ProtocolExitCodes
{
    public const int ValidProtocolResult = 0;
    public const int FatalProtocolError = 1;
    public const int RequestValidationRejected = 2;
    public const int ExecutionNotImplemented = 3;
    public const int ParentNotImplemented = 10;
}

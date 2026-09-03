using System.Text.Json;

namespace Mimir.Catalog.BenchmarkCli.Evidence;

public sealed record EvidenceRunJson(
    string EvidenceSchemaVersion,
    string ProtocolVersion,
    string CandidateId,
    string CandidateConfigId,
    string WorkloadId,
    string CorpusId,
    string RunId);

public sealed record ManifestArtifact(string RelativePath, long Bytes, string Sha256);

public sealed record EvidenceManifest(
    string EvidenceSchemaVersion,
    string CandidateId,
    string CandidateConfigId,
    string WorkloadId,
    string CorpusId,
    string RunId,
    IReadOnlyList<ManifestArtifact> Artifacts);

/// <summary>
/// Evidence-specific strict JSON. Deterministic writers (UTF-8 no BOM, compact,
/// explicit property order, one trailing LF) and strict readers that reject
/// unknown/duplicate/missing properties and malformed content. Never coupled to
/// child ProtocolJson.
/// </summary>
public static class EvidenceJson
{
    public static bool IsValidSha256(string sha)
    {
        if (sha.Length != 64) return false;
        foreach (char c in sha)
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f'))
                return false;
        return true;
    }

    public static byte[] SerializeRunJson(EvidenceRunJson run)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("evidence_schema_version", run.EvidenceSchemaVersion);
            w.WriteString("protocol_version", run.ProtocolVersion);
            w.WriteString("candidate_id", run.CandidateId);
            w.WriteString("candidate_config_id", run.CandidateConfigId);
            w.WriteString("workload_id", run.WorkloadId);
            w.WriteString("corpus_id", run.CorpusId);
            w.WriteString("run_id", run.RunId);
            w.WriteEndObject();
        }
        ms.WriteByte(0x0A);
        return ms.ToArray();
    }

    public static byte[] SerializeManifest(EvidenceManifest manifest)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("evidence_schema_version", manifest.EvidenceSchemaVersion);
            w.WriteString("candidate_id", manifest.CandidateId);
            w.WriteString("candidate_config_id", manifest.CandidateConfigId);
            w.WriteString("workload_id", manifest.WorkloadId);
            w.WriteString("corpus_id", manifest.CorpusId);
            w.WriteString("run_id", manifest.RunId);
            w.WritePropertyName("artifacts");
            w.WriteStartArray();
            foreach (var a in manifest.Artifacts)
            {
                w.WriteStartObject();
                w.WriteString("relative_path", a.RelativePath);
                w.WriteNumber("bytes", a.Bytes);
                w.WriteString("sha256", a.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        ms.WriteByte(0x0A);
        return ms.ToArray();
    }

    /// <summary>Strict run.json reader: exact names, no duplicates/unknowns, all fields once.</summary>
    public static EvidenceRunJson ReadRunJson(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw Strict("run.json must be a JSON object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? schema = null, proto = null, cand = null, cfg = null, wk = null, corpus = null, run = null;
        while (true)
        {
            if (!reader.Read()) throw Strict("unexpected end in run.json");
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw Strict("unexpected token in run.json");
            string name = reader.GetString()!;
            if (!seen.Add(name)) throw Strict($"duplicate property '{name}' in run.json");
            if (!reader.Read()) throw Strict("missing value in run.json");
            switch (name)
            {
                case "evidence_schema_version": schema = Str(ref reader, name); break;
                case "protocol_version": proto = Str(ref reader, name); break;
                case "candidate_id": cand = Str(ref reader, name); break;
                case "candidate_config_id": cfg = Str(ref reader, name); break;
                case "workload_id": wk = Str(ref reader, name); break;
                case "corpus_id": corpus = Str(ref reader, name); break;
                case "run_id": run = Str(ref reader, name); break;
                default: throw Strict($"unknown property '{name}' in run.json");
            }
        }
        if (reader.Read()) throw Strict("trailing content after run.json object");
        RequireAll(seen, "run.json", "evidence_schema_version", "protocol_version", "candidate_id",
            "candidate_config_id", "workload_id", "corpus_id", "run_id");
        return new EvidenceRunJson(schema!, proto!, cand!, cfg!, wk!, corpus!, run!);
    }

    /// <summary>Strict manifest reader: header + artifacts; artifacts strict, duplicates are a parse-level reject.</summary>
    public static EvidenceManifest ReadManifest(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw Strict("manifest must be a JSON object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? schema = null, cand = null, cfg = null, wk = null, corpus = null, run = null;
        List<ManifestArtifact>? artifacts = null;
        while (true)
        {
            if (!reader.Read()) throw Strict("unexpected end in manifest");
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw Strict("unexpected token in manifest");
            string name = reader.GetString()!;
            if (!seen.Add(name)) throw Strict($"duplicate property '{name}' in manifest");
            if (!reader.Read()) throw Strict("missing value in manifest");
            switch (name)
            {
                case "evidence_schema_version": schema = Str(ref reader, name); break;
                case "candidate_id": cand = Str(ref reader, name); break;
                case "candidate_config_id": cfg = Str(ref reader, name); break;
                case "workload_id": wk = Str(ref reader, name); break;
                case "corpus_id": corpus = Str(ref reader, name); break;
                case "run_id": run = Str(ref reader, name); break;
                case "artifacts":
                    if (reader.TokenType != JsonTokenType.StartArray) throw Strict("'artifacts' must be an array in manifest");
                    artifacts = ReadArtifacts(ref reader);
                    break;
                default: throw Strict($"unknown property '{name}' in manifest");
            }
        }
        if (reader.Read()) throw Strict("trailing content after manifest object");
        RequireAll(seen, "manifest", "evidence_schema_version", "candidate_id", "candidate_config_id",
            "workload_id", "corpus_id", "run_id", "artifacts");
        return new EvidenceManifest(schema!, cand!, cfg!, wk!, corpus!, run!, artifacts!);
    }

    private static List<ManifestArtifact> ReadArtifacts(ref Utf8JsonReader reader)
    {
        var artifacts = new List<ManifestArtifact>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return artifacts;
            if (reader.TokenType != JsonTokenType.StartObject) throw Strict("manifest artifact must be an object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? path = null;
            long? bytes = null;
            string? sha = null;
            while (true)
            {
                if (!reader.Read()) throw Strict("unexpected end in manifest artifact");
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) throw Strict("unexpected token in manifest artifact");
                string name = reader.GetString()!;
                if (!seen.Add(name)) throw Strict($"duplicate property '{name}' in manifest artifact");
                if (!reader.Read()) throw Strict("missing value in manifest artifact");
                switch (name)
                {
                    case "relative_path": path = Str(ref reader, name); break;
                    case "bytes":
                        if (!reader.TryGetInt64(out long v)) throw Strict("'bytes' must be a JSON integer");
                        bytes = v;
                        break;
                    case "sha256": sha = Str(ref reader, name); break;
                    default: throw Strict($"unknown property '{name}' in manifest artifact");
                }
            }
            RequireAll(seen, "artifact", "relative_path", "bytes", "sha256");
            artifacts.Add(new ManifestArtifact(path!, bytes!.Value, sha!));
        }
        throw Strict("unterminated artifacts array");
    }

    private static string Str(ref Utf8JsonReader reader, string property)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw Strict($"'{property}' must be a string");
        return reader.GetString()!;
    }

    private static void RequireAll(HashSet<string> seen, string where, params string[] names)
    {
        foreach (var n in names)
            if (!seen.Contains(n)) throw Strict($"missing required property '{n}' in {where}");
    }

    private static JsonException Strict(string message) => new(message);
}

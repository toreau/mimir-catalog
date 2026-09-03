using System.Globalization;
using System.Text.Json;

namespace Mimir.Catalog.BenchmarkCli.Evidence;

internal sealed record EvidenceStateSnapshot(string State, string RunId, string CandidateId, string? Stage, string? Reason, DateTime? Utc);

/// <summary>
/// Shared strict run-state writer/parser. Wire is snake_case with fixed property
/// order: state, run_id, candidate_id, then optional stage/reason/utc, one
/// trailing LF, UTF-8 without BOM. utc must round-trip ISO-8601 (O).
/// </summary>
internal static class EvidenceState
{
    public static byte[] Serialize(string state, string runId, string candidateId, string? stage = null, string? reason = null, DateTime? utc = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("state", state);
            w.WriteString("run_id", runId);
            w.WriteString("candidate_id", candidateId);
            if (stage is not null) w.WriteString("stage", stage);
            if (reason is not null) w.WriteString("reason", reason);
            if (utc is { } u) w.WriteString("utc", u.ToString("O", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        ms.WriteByte(0x0A);
        return ms.ToArray();
    }

    public static EvidenceStateSnapshot ParseStrict(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("state must be a JSON object");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? state = null, runId = null, candidateId = null, stage = null, reason = null;
        DateTime? utc = null;

        while (true)
        {
            if (!reader.Read()) throw new JsonException("unexpected end of state");
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("unexpected token in state");
            string name = reader.GetString()!;
            if (!seen.Add(name)) throw new JsonException($"duplicate property '{name}' in state");
            if (!reader.Read()) throw new JsonException("missing value in state");
            switch (name)
            {
                case "state": state = Str(ref reader, name); break;
                case "run_id": runId = Str(ref reader, name); break;
                case "candidate_id": candidateId = Str(ref reader, name); break;
                case "stage": stage = Str(ref reader, name); break;
                case "reason": reason = Str(ref reader, name); break;
                case "utc":
                    string utcText = Str(ref reader, name);
                    if (!DateTime.TryParse(utcText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                        throw new JsonException("'utc' must be round-trip ISO-8601");
                    utc = parsed;
                    break;
                default: throw new JsonException($"unknown property '{name}' in state");
            }
        }
        if (reader.Read()) throw new JsonException("trailing content after state object");
        Require(seen, "state", "run_id", "candidate_id");
        return new EvidenceStateSnapshot(state!, runId!, candidateId!, stage, reason, utc);
    }

    private static string Str(ref Utf8JsonReader reader, string property)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"'{property}' must be a string");
        return reader.GetString()!;
    }

    private static void Require(HashSet<string> seen, params string[] names)
    {
        foreach (var n in names)
            if (!seen.Contains(n)) throw new JsonException($"missing required property '{n}' in state");
    }
}

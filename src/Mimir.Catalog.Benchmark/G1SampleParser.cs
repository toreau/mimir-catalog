using System.Text;
using System.Text.Json;

namespace Mimir.Catalog.Benchmark;

public sealed class G1SampleParseException : Exception
{
    public G1SampleParseException(string message) : base(message) { }
}

/// <summary>
/// Strict parent parser for the deterministic G1 JSONL artifact. UTF-8 no BOM,
/// LF only, one object per nonempty line, no blank records, exact known fields,
/// exact types; correctness_status is exactly VALID|INVALID|ERROR (never
/// TIMEOUT in a child artifact). Mandatory: operation/sequence/stratum/
/// wall_seconds/correctness_status. Optional: actual_cardinality,
/// actual_visited, actual_digest, error.
/// </summary>
public static class G1SampleParser
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "operation", "sequence", "stratum", "wall_seconds", "correctness_status",
        "actual_cardinality", "actual_visited", "actual_digest", "error",
    };

    public static IReadOnlyList<G1TimedSample> Parse(byte[] bytes)
    {
        if (bytes.Length == 0) return Array.Empty<G1TimedSample>();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new G1SampleParseException("artifact must not contain a BOM");

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (Exception ex)
        {
            throw new G1SampleParseException($"invalid UTF-8: {ex.Message}");
        }

        if (text.IndexOf('\r') >= 0)
            throw new G1SampleParseException("artifact must use LF line endings only (no CR/CRLF)");
        if (bytes[^1] != 0x0A)
            throw new G1SampleParseException("non-empty artifact must end with a final LF");

        string[] lines = text.Split('\n');
        var samples = new List<G1TimedSample>();
        for (int i = 0; i < lines.Length - 1; i++)
        {
            string line = lines[i];
            if (line.Length == 0) throw new G1SampleParseException($"blank record at line {i + 1}");
            samples.Add(ParseLine(line, i + 1));
        }
        return samples;
    }

    private static G1TimedSample ParseLine(string json, int lineNumber)
    {
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new G1SampleParseException($"line {lineNumber}: record must be a JSON object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string operation = "";
            long sequence = 0;
            string stratum = "";
            double wall = 0;
            string status = "";
            long? cardinality = null;
            long? visited = null;
            string? digest = null;
            string? error = null;
            bool hadOperation = false, hadSequence = false, hadStratum = false, hadWall = false, hadStatus = false;

            while (true)
            {
                if (!reader.Read()) throw new G1SampleParseException($"line {lineNumber}: unterminated object");
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new G1SampleParseException($"line {lineNumber}: unexpected token");
                string name = reader.GetString()!;
                if (!seen.Add(name)) throw new G1SampleParseException($"line {lineNumber}: duplicate property '{name}'");
                if (!Known.Contains(name)) throw new G1SampleParseException($"line {lineNumber}: unknown property '{name}'");
                if (!reader.Read()) throw new G1SampleParseException($"line {lineNumber}: missing value");
                switch (name)
                {
                    case "operation": operation = Str(reader, name, lineNumber); hadOperation = true; break;
                    case "sequence":
                        if (!reader.TryGetInt64(out sequence)) throw new G1SampleParseException($"line {lineNumber}: 'sequence' must be an integer");
                        hadSequence = true;
                        break;
                    case "stratum": stratum = Str(reader, name, lineNumber); hadStratum = true; break;
                    case "wall_seconds":
                        if (!reader.TryGetDouble(out wall) || !double.IsFinite(wall) || wall < 0)
                            throw new G1SampleParseException($"line {lineNumber}: 'wall_seconds' must be a finite non-negative number");
                        hadWall = true;
                        break;
                    case "correctness_status": status = Str(reader, name, lineNumber); hadStatus = true; break;
                    case "actual_cardinality":
                        if (reader.TokenType == JsonTokenType.Null) { cardinality = null; break; }
                        if (!reader.TryGetInt64(out long card)) throw new G1SampleParseException($"line {lineNumber}: 'actual_cardinality' must be an integer");
                        cardinality = card;
                        break;
                    case "actual_visited":
                        if (reader.TokenType == JsonTokenType.Null) { visited = null; break; }
                        if (!reader.TryGetInt64(out long vis)) throw new G1SampleParseException($"line {lineNumber}: 'actual_visited' must be an integer");
                        visited = vis;
                        break;
                    case "actual_digest":
                        digest = reader.TokenType == JsonTokenType.Null ? null : Str(reader, name, lineNumber);
                        break;
                    case "error":
                        error = reader.TokenType == JsonTokenType.Null ? null : Str(reader, name, lineNumber);
                        break;
                }
            }
            if (reader.Read()) throw new G1SampleParseException($"line {lineNumber}: trailing content");

            if (!hadOperation || operation.Length == 0) throw new G1SampleParseException($"line {lineNumber}: missing required 'operation'");
            if (!hadSequence) throw new G1SampleParseException($"line {lineNumber}: missing required 'sequence'");
            if (!hadStratum || stratum.Length == 0) throw new G1SampleParseException($"line {lineNumber}: missing required 'stratum'");
            if (!hadWall) throw new G1SampleParseException($"line {lineNumber}: missing required 'wall_seconds'");
            if (!hadStatus) throw new G1SampleParseException($"line {lineNumber}: missing required 'correctness_status'");
            if (status is not (ServingStatuses.Valid or ServingStatuses.Invalid or ServingStatuses.Error))
                throw new G1SampleParseException($"line {lineNumber}: invalid correctness_status '{status}'");
            return new G1TimedSample(operation, sequence, stratum, wall, status, cardinality, visited, digest, error);
        }
        catch (G1SampleParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new G1SampleParseException($"line {lineNumber}: malformed JSON: {ex.Message}");
        }
    }

    private static string Str(in Utf8JsonReader reader, string name, int line)
    {
        if (reader.TokenType != JsonTokenType.String) throw new G1SampleParseException($"line {line}: '{name}' must be a string");
        return reader.GetString()!;
    }
}

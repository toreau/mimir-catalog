using System.Text;
using System.Text.Json;

namespace Mimir.Catalog.Benchmark;

public sealed class ServingSampleParseException : Exception
{
    public ServingSampleParseException(string message) : base(message) { }
}

/// <summary>
/// Strict parent parser for the deterministic serving JSONL artifact emitted by
/// the 4d.2a child. UTF-8 no BOM, exactly one JSON object per nonempty line, no
/// blank records, exact known fields, exact types; correctness_status is
/// exactly VALID|INVALID|ERROR (never TIMEOUT in a child artifact).
/// </summary>
public static class ServingSampleParser
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "operation", "sequence", "stratum", "wall_seconds", "correctness_status",
        "actual_cardinality", "actual_digest", "error",
    };

    public static IReadOnlyList<ServingTimedSample> Parse(byte[] bytes)
    {
        if (bytes.Length == 0) return Array.Empty<ServingTimedSample>();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new ServingSampleParseException("artifact must not contain a BOM");

        string text;
        try
        {
            // Strict UTF-8: invalid byte sequences throw instead of being replaced.
            text = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ServingSampleParseException($"invalid UTF-8: {ex.Message}");
        }
        catch (Exception ex)
        {
            throw new ServingSampleParseException($"invalid UTF-8: {ex.Message}");
        }

        if (text.IndexOf('\r') >= 0)
            throw new ServingSampleParseException("artifact must use LF line endings only (no CR/CRLF)");
        if (bytes[^1] != 0x0A)
            throw new ServingSampleParseException("non-empty artifact must end with a final LF");

        string[] lines = text.Split('\n');
        // Writer convention: every record is LF-terminated, so the last element is empty.
        var samples = new List<ServingTimedSample>();
        for (int i = 0; i < lines.Length - 1; i++)
        {
            string line = lines[i];
            if (line.Length == 0) throw new ServingSampleParseException($"blank record at line {i + 1}");
            samples.Add(ParseLine(line, i + 1));
        }
        return samples;
    }

    private static ServingTimedSample ParseLine(string json, int lineNumber)
    {
        Utf8JsonReader reader;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new ServingSampleParseException($"line {lineNumber}: record must be a JSON object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string operation = "";
            long sequence = 0;
            string stratum = "";
            double wall = 0;
            string status = "";
            bool hadOperation = false, hadSequence = false, hadStratum = false, hadWall = false, hadStatus = false;
            long? cardinality = null;
            string? digest = null;
            string? error = null;
            while (true)
            {
                if (!reader.Read()) throw new ServingSampleParseException($"line {lineNumber}: unterminated object");
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new ServingSampleParseException($"line {lineNumber}: unexpected token");
                string name = reader.GetString()!;
                if (!seen.Add(name)) throw new ServingSampleParseException($"line {lineNumber}: duplicate property '{name}'");
                if (!Known.Contains(name)) throw new ServingSampleParseException($"line {lineNumber}: unknown property '{name}'");
                if (!reader.Read()) throw new ServingSampleParseException($"line {lineNumber}: missing value");
                switch (name)
                {
                    case "operation": operation = Str(reader, name, lineNumber); hadOperation = true; break;
                    case "sequence":
                        if (!reader.TryGetInt64(out sequence)) throw new ServingSampleParseException($"line {lineNumber}: 'sequence' must be an integer");
                        hadSequence = true;
                        break;
                    case "stratum": stratum = Str(reader, name, lineNumber); hadStratum = true; break;
                    case "wall_seconds":
                        if (!reader.TryGetDouble(out wall) || !double.IsFinite(wall) || wall < 0)
                            throw new ServingSampleParseException($"line {lineNumber}: 'wall_seconds' must be a finite non-negative number");
                        hadWall = true;
                        break;
                    case "correctness_status": status = Str(reader, name, lineNumber); hadStatus = true; break;
                    case "actual_cardinality":
                        if (reader.TokenType == JsonTokenType.Null) { cardinality = null; break; }
                        if (!reader.TryGetInt64(out long card)) throw new ServingSampleParseException($"line {lineNumber}: 'actual_cardinality' must be an integer");
                        cardinality = card;
                        break;
                    case "actual_digest":
                        digest = reader.TokenType == JsonTokenType.Null ? null : Str(reader, name, lineNumber);
                        break;
                    case "error":
                        error = reader.TokenType == JsonTokenType.Null ? null : Str(reader, name, lineNumber);
                        break;
                }
            }
            if (reader.Read()) throw new ServingSampleParseException($"line {lineNumber}: trailing content");

            if (!hadOperation || operation.Length == 0) throw new ServingSampleParseException($"line {lineNumber}: missing required 'operation'");
            if (!hadSequence) throw new ServingSampleParseException($"line {lineNumber}: missing required 'sequence'");
            if (!hadStratum || stratum.Length == 0) throw new ServingSampleParseException($"line {lineNumber}: missing required 'stratum'");
            if (!hadWall) throw new ServingSampleParseException($"line {lineNumber}: missing required 'wall_seconds'");
            if (!hadStatus) throw new ServingSampleParseException($"line {lineNumber}: missing required 'correctness_status'");
            if (status is not (ServingStatuses.Valid or ServingStatuses.Invalid or ServingStatuses.Error))
                throw new ServingSampleParseException($"line {lineNumber}: invalid correctness_status '{status}'");
            return new ServingTimedSample(operation, sequence, stratum, wall, status, cardinality, digest, error);
        }
        catch (ServingSampleParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ServingSampleParseException($"line {lineNumber}: malformed JSON: {ex.Message}");
        }
    }

    private static string Str(in Utf8JsonReader reader, string name, int line)
    {
        if (reader.TokenType != JsonTokenType.String) throw new ServingSampleParseException($"line {line}: '{name}' must be a string");
        return reader.GetString()!;
    }
}

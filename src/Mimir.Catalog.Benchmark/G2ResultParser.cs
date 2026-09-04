using System.Text;
using System.Text.Json;

namespace Mimir.Catalog.Benchmark;

public sealed class G2ResultParseException : Exception
{
    public G2ResultParseException(string message) : base(message) { }
}

/// <summary>
/// Strict writer-exact parser for the deterministic G2 JSONL artifact. UTF-8 no
/// BOM, LF only, one object per nonempty line, final LF when non-empty, zero-byte
/// valid. Exactly two kinds: per-input then one optional Batch as the final
/// record. Optional fields that the child writer omits when null are rejected if
/// present as explicit JSON null (actual_cardinality/actual_digest/error).
/// </summary>
public static class G2ResultParser
{
    private static readonly HashSet<string> PerInputKnown = new(StringComparer.Ordinal)
    {
        "kind", "operation", "sequence", "item", "qid", "source_stratum",
        "correctness_status", "actual_cardinality", "actual_digest", "error",
    };

    private static readonly HashSet<string> BatchKnown = new(StringComparer.Ordinal)
    {
        "kind", "operation", "sequence", "wall_seconds",
        "correctness_status", "actual_cardinality", "actual_digest", "error",
    };

    public static G2RawDocument Parse(byte[] bytes)
    {
        if (bytes.Length == 0) return new G2RawDocument(Array.Empty<G2RawPerInput>(), null);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new G2ResultParseException("artifact must not contain a BOM");

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (Exception ex)
        {
            throw new G2ResultParseException($"invalid UTF-8: {ex.Message}");
        }

        if (text.IndexOf('\r') >= 0)
            throw new G2ResultParseException("artifact must use LF line endings only (no CR/CRLF)");
        if (bytes[^1] != 0x0A)
            throw new G2ResultParseException("non-empty artifact must end with a final LF");

        var perInput = new List<G2RawPerInput>();
        G2RawBatch? batch = null;
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length - 1; i++)
        {
            string line = lines[i];
            if (line.Length == 0) throw new G2ResultParseException($"blank record at line {i + 1}");
            (string kind, object parsed) = ParseLine(line, i + 1);
            if (kind == "batch")
            {
                if (batch is not null)
                    throw new G2ResultParseException($"multiple Batch records; line {i + 1}");
                batch = (G2RawBatch)parsed;
            }
            else
            {
                if (batch is not null)
                    throw new G2ResultParseException($"record after Batch at line {i + 1}");
                perInput.Add((G2RawPerInput)parsed);
            }
        }
        return new G2RawDocument(perInput, batch);
    }

    private static (string, object) ParseLine(string json, int lineNumber)
    {
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new G2ResultParseException($"line {lineNumber}: record must be a JSON object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? kind = null;
            string operation = "";
            long sequence = 0;
            string status = "";
            int item = 0;
            long qid = 0;
            string? sourceStratum = null;
            double wall = 0;
            long? cardinality = null;
            string? digest = null;
            string? error = null;
            bool hadOperation = false, hadSequence = false, hadStatus = false, hadItem = false, hadQid = false;
            bool hadSourceStratum = false, hadWall = false, hadCard = false, hadDigest = false, hadError = false;

            while (true)
            {
                if (!reader.Read()) throw new G2ResultParseException($"line {lineNumber}: unterminated object");
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new G2ResultParseException($"line {lineNumber}: unexpected token");
                string name = reader.GetString()!;
                if (!seen.Add(name)) throw new G2ResultParseException($"line {lineNumber}: duplicate property '{name}'");
                if (!reader.Read()) throw new G2ResultParseException($"line {lineNumber}: missing value");

                if (name == "kind")
                {
                    kind = Str(reader, name, lineNumber);
                    if (kind is not ("per-input" or "batch"))
                        throw new G2ResultParseException($"line {lineNumber}: invalid kind '{kind}'");
                    continue;
                }

                var known = kind == "batch" ? BatchKnown : PerInputKnown;
                if (kind is null) throw new G2ResultParseException($"line {lineNumber}: 'kind' must be the first property");
                if (!known.Contains(name))
                    throw new G2ResultParseException($"line {lineNumber}: unknown or wrong-kind property '{name}'");

                switch (name)
                {
                    case "operation": operation = Str(reader, name, lineNumber); hadOperation = true; break;
                    case "sequence":
                        if (!reader.TryGetInt64(out sequence)) throw new G2ResultParseException($"line {lineNumber}: 'sequence' must be an integer");
                        hadSequence = true;
                        break;
                    case "item":
                        if (!reader.TryGetInt32(out item)) throw new G2ResultParseException($"line {lineNumber}: 'item' must be an integer");
                        hadItem = true;
                        break;
                    case "qid":
                        if (!reader.TryGetInt64(out qid)) throw new G2ResultParseException($"line {lineNumber}: 'qid' must be an integer");
                        hadQid = true;
                        break;
                    case "source_stratum": sourceStratum = Str(reader, name, lineNumber); hadSourceStratum = true; break;
                    case "wall_seconds":
                        if (!reader.TryGetDouble(out wall) || !double.IsFinite(wall) || wall < 0)
                            throw new G2ResultParseException($"line {lineNumber}: 'wall_seconds' must be a finite non-negative number");
                        hadWall = true;
                        break;
                    case "correctness_status": status = Str(reader, name, lineNumber); hadStatus = true; break;
                    case "actual_cardinality":
                        if (reader.TokenType == JsonTokenType.Null) throw new G2ResultParseException($"line {lineNumber}: explicit null is not allowed for '{name}'");
                        if (!reader.TryGetInt64(out long card)) throw new G2ResultParseException($"line {lineNumber}: 'actual_cardinality' must be an integer");
                        cardinality = card; hadCard = true;
                        break;
                    case "actual_digest":
                        if (reader.TokenType == JsonTokenType.Null) throw new G2ResultParseException($"line {lineNumber}: explicit null is not allowed for '{name}'");
                        digest = Str(reader, name, lineNumber); hadDigest = true;
                        break;
                    case "error":
                        if (reader.TokenType == JsonTokenType.Null) throw new G2ResultParseException($"line {lineNumber}: explicit null is not allowed for '{name}'");
                        error = Str(reader, name, lineNumber); hadError = true;
                        break;
                }
            }
            if (reader.Read()) throw new G2ResultParseException($"line {lineNumber}: trailing content");

            if (operation != "G2") throw new G2ResultParseException($"line {lineNumber}: operation must be G2");
            if (!hadSequence || sequence != 500) throw new G2ResultParseException($"line {lineNumber}: sequence must be 500");
            if (!hadStatus || status is not (ServingStatuses.Valid or ServingStatuses.Invalid or ServingStatuses.Error))
                throw new G2ResultParseException($"line {lineNumber}: invalid correctness_status");

            if (kind == "batch")
            {
                if (!hadWall) throw new G2ResultParseException($"line {lineNumber}: Batch requires 'wall_seconds'");
                if (hadItem || hadQid || hadSourceStratum)
                    throw new G2ResultParseException($"line {lineNumber}: Batch must not carry per-input fields");
                return ("batch", new G2RawBatch(wall, status, cardinality, digest, error));
            }

            if (!hadItem || !hadQid || !hadSourceStratum)
                throw new G2ResultParseException($"line {lineNumber}: per-input requires item/qid/source_stratum");
            if (hadWall) throw new G2ResultParseException($"line {lineNumber}: per-input must not carry 'wall_seconds'");
            return ("per-input", new G2RawPerInput(item, qid, sourceStratum!, status, cardinality, digest, error));
        }
        catch (G2ResultParseException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new G2ResultParseException($"line {lineNumber}: malformed JSON: {ex.Message}");
        }
    }

    private static string Str(in Utf8JsonReader reader, string name, int line)
    {
        if (reader.TokenType != JsonTokenType.String) throw new G2ResultParseException($"line {line}: '{name}' must be a string");
        return reader.GetString()!;
    }
}

using System.Text.Json;

namespace Mimir.Catalog.Corpus;

public enum EntityOutcome
{
    /// <summary>JSON object with an id that is neither a valid item nor a valid property (or invalid id form).</summary>
    Malformed,
    /// <summary>Valid property source record (type == property, valid P id). Never projected.</summary>
    NonItem,
    /// <summary>Provider missing/deleted representation (defensive; not emitted by the dump).</summary>
    Missing,
    /// <summary>Valid item (type == item, valid Q id).</summary>
    Item,
    /// <summary>Line that is not an entity record at all (brackets, whitespace, incomplete non-JSON tail). Not counted.</summary>
    Ignore,
}

/// <summary>Projected payload of a single valid item, mirroring the Phase-0 record semantics.</summary>
public sealed class ParsedItem
{
    public long Qid { get; set; }
    public bool LabelEnPresent { get; set; }
    public bool LabelNbPresent { get; set; }
    public string? LabelEnValue { get; set; }
    public string? LabelNbValue { get; set; }
    /// <summary>Distinct alias values for en, mirroring Phase-0 set semantics.</summary>
    public List<string> AliasEn { get; set; } = new();
    public List<string> AliasNb { get; set; } = new();
    /// <summary>Distinct P31 targets as numeric Qids (Phase-0 per-entity duplicate elimination).</summary>
    public List<long> P31Targets { get; set; } = new();
    /// <summary>Distinct P279 targets as numeric Qids.</summary>
    public List<long> P279Targets { get; set; } = new();
}

public sealed class EntityParseResult
{
    public required EntityOutcome Outcome { get; init; }
    /// <summary>True when the line decoded to a JSON object carrying an id (Phase-0 source_records).</summary>
    public bool CountsAsSourceRecord { get; init; }
    public ParsedItem? Item { get; init; }
}

/// <summary>
/// Parses one trimmed Wikidata JSON dump entity line with Phase-0 semantic
/// equivalence (see scripts/0c_strategy_a.py iter_entity_lines + claim_targets).
/// </summary>
public static class EntityParser
{
    public static EntityParseResult Parse(string jsonLine)
    {
        if (jsonLine.EndsWith(','))
            jsonLine = jsonLine[..^1];
        if (!jsonLine.StartsWith("{"))
            return new EntityParseResult { Outcome = EntityOutcome.Ignore };

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonLine);
        }
        catch (JsonException)
        {
            return new EntityParseResult { Outcome = EntityOutcome.Malformed };
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new EntityParseResult { Outcome = EntityOutcome.Malformed };

            if (root.TryGetProperty("missing", out _))
                return new EntityParseResult { Outcome = EntityOutcome.Missing };

            if (!root.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.String)
                return new EntityParseResult { Outcome = EntityOutcome.Malformed };

            string id = idEl.GetString() ?? string.Empty;
            string type = root.TryGetProperty("type", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString()!
                : string.Empty;

            if (type == "item" && Qid.IsValidItemId(id))
            {
                if (!Qid.TryParse(id, out long qid))
                    return new EntityParseResult { Outcome = EntityOutcome.Malformed, CountsAsSourceRecord = true };
                return BuildItem(root, qid);
            }

            if (type == "property" && Qid.IsValidPropertyId(id))
                return new EntityParseResult { Outcome = EntityOutcome.NonItem, CountsAsSourceRecord = true };

            return new EntityParseResult { Outcome = EntityOutcome.Malformed, CountsAsSourceRecord = true };
        }
    }

    private static EntityParseResult BuildItem(JsonElement root, long qid)
    {
        var item = new ParsedItem { Qid = qid };

        if (root.TryGetProperty("labels", out JsonElement labels) && labels.ValueKind == JsonValueKind.Object)
        {
            item.LabelEnPresent = TryLabel(labels, "en", out string? enVal);
            if (item.LabelEnPresent) item.LabelEnValue = enVal;
            item.LabelNbPresent = TryLabel(labels, "nb", out string? nbVal);
            if (item.LabelNbPresent) item.LabelNbValue = nbVal;
        }

        if (root.TryGetProperty("aliases", out JsonElement aliases) && aliases.ValueKind == JsonValueKind.Object)
        {
            item.AliasEn = AliasValues(aliases, "en");
            item.AliasNb = AliasValues(aliases, "nb");
        }

        if (root.TryGetProperty("claims", out JsonElement claims) && claims.ValueKind == JsonValueKind.Object)
        {
            item.P31Targets = ClaimTargets(claims, "P31");
            item.P279Targets = ClaimTargets(claims, "P279");
        }

        return new EntityParseResult { Outcome = EntityOutcome.Item, CountsAsSourceRecord = true, Item = item };
    }

    /// <summary>Presence means a non-empty language object under the language key (Phase-0 truthy dict).</summary>
    private static bool TryLabel(JsonElement labels, string lang, out string? value)
    {
        value = null;
        if (!labels.TryGetProperty(lang, out JsonElement entry) || entry.ValueKind != JsonValueKind.Object)
            return false;
        bool nonEmpty = entry.EnumerateObject().MoveNext();
        if (!nonEmpty)
            return false;
        if (entry.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.String)
            value = v.GetString();
        else
            value = string.Empty;
        return true;
    }

    private static List<string> AliasValues(JsonElement aliases, string lang)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!aliases.TryGetProperty(lang, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();
        foreach (JsonElement el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;
            string value = el.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;
            set.Add(value);
        }
        return set.ToList();
    }

    /// <summary>Phase-0 claim_targets: mainsnak value whose datavalue type is wikibase-entityid; target = numeric-id.</summary>
    private static List<long> ClaimTargets(JsonElement claims, string prop)
    {
        var set = new HashSet<long>();
        if (!claims.TryGetProperty(prop, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<long>();
        foreach (JsonElement claim in arr.EnumerateArray())
        {
            if (claim.ValueKind != JsonValueKind.Object)
                continue;
            if (!claim.TryGetProperty("mainsnak", out JsonElement main) || main.ValueKind != JsonValueKind.Object)
                continue;
            if (!main.TryGetProperty("snaktype", out JsonElement st) || st.GetString() != "value")
                continue;
            if (!main.TryGetProperty("datavalue", out JsonElement dv) || dv.ValueKind != JsonValueKind.Object)
                continue;
            if (!dv.TryGetProperty("type", out JsonElement dt) || dt.GetString() != "wikibase-entityid")
                continue;
            if (!dv.TryGetProperty("value", out JsonElement val) || val.ValueKind != JsonValueKind.Object)
                continue;
            if (!val.TryGetProperty("numeric-id", out JsonElement num) ||
                !num.TryGetInt64(out long numeric))
                continue;
            set.Add(numeric);
        }
        return set.ToList();
    }
}

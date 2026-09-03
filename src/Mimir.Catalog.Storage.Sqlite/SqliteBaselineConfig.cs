using System.Text.Json;
using Canon = Mimir.Catalog.Workload.Canon;

namespace Mimir.Catalog.Storage.Sqlite;

/// <summary>
/// Parsed, validated Candidate A baseline configuration (frozen in
/// benchmarks/candidate-a-sqlite-v1.json). Canonical semantic encoding drives
/// the Candidate Config ID; JSON whitespace/ordering never does. This config
/// ID is separate from the workload ID.
/// </summary>
public sealed class SqliteBaselineConfig
{
    public const string Schema = "mimir-catalog-candidate-a-sqlite-v1";
    public const int SchemaVersion = 1;
    public const string ProviderName = "Microsoft.Data.Sqlite";
    public const string ProviderVersion = "10.0.11";
    public const string CorpusId = "511adb9ebd066f1d4d344b80171902d5";
    public const string WorkloadId = "cc85bd20801b8239fa5f4374588d83ff5b5cb7ec482bbccd3e7fb03d283513fc";

    public sealed record ColumnDef(string Name, string Type, bool NotNull, bool PrimaryKey = false, string? Collation = null);

    public sealed record TableDef(string Name, string Organization, IReadOnlyList<ColumnDef> Columns, bool Strict);

    public sealed record IndexDef(string Name, string Table, IReadOnlyList<string> Columns);

    public int SchemaVer { get; init; } = SchemaVersion;
    public string ProviderNameValue { get; init; } = ProviderName;
    public string ProviderVersionValue { get; init; } = ProviderVersion;
    public string CorpusIdValue { get; init; } = CorpusId;
    public string WorkloadIdValue { get; init; } = WorkloadId;

    public int BuildPageSize { get; init; } = 4096;
    public string BuildJournalMode { get; init; } = "OFF";
    public string BuildSynchronous { get; init; } = "OFF";
    public string BuildForeignKeys { get; init; } = "OFF";
    public string BuildAutomaticIndex { get; init; } = "OFF";
    public string BuildTempStore { get; init; } = "DEFAULT"; // unset at the connection

    public string ReadForeignKeys { get; init; } = "OFF";
    public string ReadAutomaticIndex { get; init; } = "OFF";

    public string AnalyzePolicy { get; init; } = "none";
    public string VacuumPolicy { get; init; } = "none";
    public string OptimizePolicy { get; init; } = "none";

    public IReadOnlyList<TableDef> Tables { get; init; } = DefaultTables();
    public IReadOnlyList<IndexDef> Indexes { get; init; } = DefaultIndexes();

    public static IReadOnlyList<TableDef> DefaultTables() =>
    [
        new("concept", "rowid-primary-key",
        [
            new("Qid", "INTEGER", NotNull: true, PrimaryKey: true),
            new("InT1", "INTEGER", NotNull: true),
            new("InT2", "INTEGER", NotNull: true),
        ], Strict: true),
        new("lexical_entry", "rowid",
        [
            new("Qid", "INTEGER", NotNull: true),
            new("Lang", "TEXT", NotNull: true, Collation: "BINARY"),
            new("LexKind", "TEXT", NotNull: true, Collation: "BINARY"),
            new("Value", "TEXT", NotNull: true, Collation: "BINARY"),
        ], Strict: true),
        new("instance_of", "rowid",
        [
            new("SubjectQid", "INTEGER", NotNull: true),
            new("TargetQid", "INTEGER", NotNull: true),
        ], Strict: true),
        new("subclass_of", "rowid",
        [
            new("SubjectQid", "INTEGER", NotNull: true),
            new("TargetQid", "INTEGER", NotNull: true),
        ], Strict: true),
    ];

    public static IReadOnlyList<IndexDef> DefaultIndexes() =>
    [
        new("lex_lang_value", "lexical_entry", ["Lang COLLATE BINARY", "Value COLLATE BINARY"]),
        new("lex_qid", "lexical_entry", ["Qid", "Lang COLLATE BINARY", "LexKind COLLATE BINARY"]),
        new("inst_subject", "instance_of", ["SubjectQid", "TargetQid"]),
        new("sub_subject", "subclass_of", ["SubjectQid", "TargetQid"]),
    ];

    public static SqliteBaselineConfig Default() => new();

    private static readonly HashSet<string> KnownTop = new(StringComparer.Ordinal)
    {
        "schema", "schemaVersion", "providerName", "providerVersion", "corpusId", "workloadId",
        "buildPragmas", "readPragmas", "analyzePolicy", "vacuumPolicy", "optimizePolicy", "tables", "indexes",
    };

    public static SqliteBaselineConfig Parse(byte[] json)
    {
        try
        {
            return ParseCore(json);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or JsonException)
        {
            throw new InvalidDataException($"candidate config invalid: {ex.Message}");
        }
    }

    private static SqliteBaselineConfig ParseCore(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) throw new InvalidDataException("candidate config root must be an object");
        foreach (var p in r.EnumerateObject())
            if (!KnownTop.Contains(p.Name)) throw new InvalidDataException($"unknown candidate config field: {p.Name}");

        string schema = r.GetProperty("schema").GetString() ?? string.Empty;
        if (schema != Schema) throw new InvalidDataException($"candidate config schema mismatch: {schema}");

        string req(string name) => r.GetProperty(name).GetString() ?? throw new InvalidDataException($"missing string field {name}");

        var build = r.GetProperty("buildPragmas");
        var read = r.GetProperty("readPragmas");
        foreach (var p in build.EnumerateObject())
            if (!BuildPragmaKeys.Contains(p.Name)) throw new InvalidDataException($"unknown build pragma {p.Name}");
        foreach (var p in read.EnumerateObject())
            if (!ReadPragmaKeys.Contains(p.Name)) throw new InvalidDataException($"unknown read pragma {p.Name}");

        var config = new SqliteBaselineConfig
        {
            SchemaVer = r.GetProperty("schemaVersion").GetInt32(),
            ProviderNameValue = req("providerName"),
            ProviderVersionValue = req("providerVersion"),
            CorpusIdValue = req("corpusId"),
            WorkloadIdValue = req("workloadId"),
            BuildPageSize = build.GetProperty("page_size").GetInt32(),
            BuildJournalMode = reqPragma(build, "journal_mode"),
            BuildSynchronous = reqPragma(build, "synchronous"),
            BuildForeignKeys = reqPragma(build, "foreign_keys"),
            BuildAutomaticIndex = reqPragma(build, "automatic_index"),
            BuildTempStore = reqPragma(build, "temp_store"),
            ReadForeignKeys = reqPragma(read, "foreign_keys"),
            ReadAutomaticIndex = reqPragma(read, "automatic_index"),
            AnalyzePolicy = req("analyzePolicy"),
            VacuumPolicy = req("vacuumPolicy"),
            OptimizePolicy = req("optimizePolicy"),
            Tables = ParseTables(r.GetProperty("tables")),
            Indexes = ParseIndexes(r.GetProperty("indexes")),
        };

        Validate(config);
        return config;
    }

    private static readonly HashSet<string> BuildPragmaKeys = new(StringComparer.Ordinal)
    {
        "page_size", "journal_mode", "synchronous", "foreign_keys", "automatic_index", "temp_store",
    };

    private static readonly HashSet<string> ReadPragmaKeys = new(StringComparer.Ordinal)
    {
        "foreign_keys", "automatic_index",
    };

    private static readonly HashSet<string> TableKeys = new(StringComparer.Ordinal)
    {
        "name", "organization", "strict", "columns",
    };

    private static readonly HashSet<string> ColumnKeys = new(StringComparer.Ordinal)
    {
        "name", "type", "notNull", "primaryKey", "collation",
    };

    private static readonly HashSet<string> IndexKeys = new(StringComparer.Ordinal)
    {
        "name", "table", "columns",
    };

    private static string reqPragma(JsonElement obj, string name) =>
        obj.GetProperty(name).GetString() ?? throw new InvalidDataException($"missing pragma {name}");

    private static void RejectUnknownKeys(JsonElement obj, HashSet<string> allowed, string context)
    {
        foreach (var p in obj.EnumerateObject())
            if (!allowed.Contains(p.Name)) throw new InvalidDataException($"unknown {context} field: {p.Name}");
    }

    private static List<TableDef> ParseTables(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) throw new InvalidDataException("tables must be an array");
        var list = new List<TableDef>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) throw new InvalidDataException("table must be an object");
            RejectUnknownKeys(el, TableKeys, "table");
            var colArr = el.GetProperty("columns");
            if (colArr.ValueKind != JsonValueKind.Array) throw new InvalidDataException("table columns must be an array");
            var cols = new List<ColumnDef>();
            foreach (var c in colArr.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) throw new InvalidDataException("column must be an object");
                RejectUnknownKeys(c, ColumnKeys, "column");
                cols.Add(new ColumnDef(c.GetProperty("name").GetString()!, c.GetProperty("type").GetString()!,
                    c.GetProperty("notNull").GetBoolean(),
                    c.TryGetProperty("primaryKey", out var pk) && pk.ValueKind == JsonValueKind.True,
                    c.TryGetProperty("collation", out var co) && co.ValueKind == JsonValueKind.String ? co.GetString() : null));
            }
            list.Add(new TableDef(el.GetProperty("name").GetString()!, el.GetProperty("organization").GetString()!, cols,
                el.TryGetProperty("strict", out var st) && st.GetBoolean()));
        }
        return list;
    }

    private static List<IndexDef> ParseIndexes(JsonElement arr)
    {
        var list = new List<IndexDef>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) throw new InvalidDataException("index must be an object");
            RejectUnknownKeys(el, IndexKeys, "index");
            var colArr = el.GetProperty("columns");
            if (colArr.ValueKind != JsonValueKind.Array) throw new InvalidDataException("index columns must be an array");
            var cols = colArr.EnumerateArray().Select(c => c.GetString()!).ToList();
            list.Add(new IndexDef(el.GetProperty("name").GetString()!, el.GetProperty("table").GetString()!, cols));
        }
        return list;
    }

    public static void Validate(SqliteBaselineConfig c)
    {
        if (c.SchemaVer != SchemaVersion) throw new InvalidDataException("unsupported candidate config schemaVersion");
        if (c.ProviderNameValue != ProviderName) throw new InvalidDataException($"unsupported provider {c.ProviderNameValue}");
        if (c.ProviderVersionValue != ProviderVersion) throw new InvalidDataException($"unsupported provider version {c.ProviderVersionValue}");
        if (c.CorpusIdValue != CorpusId) throw new InvalidDataException("candidate config corpusId mismatch");
        if (c.WorkloadIdValue != WorkloadId) throw new InvalidDataException("candidate config workloadId mismatch");
        if (c.BuildPageSize != 4096) throw new InvalidDataException("candidate config page_size must be 4096");
        if (c.BuildJournalMode != "OFF" || c.BuildSynchronous != "OFF" || c.BuildForeignKeys != "OFF"
            || c.BuildAutomaticIndex != "OFF" || c.BuildTempStore != "DEFAULT")
            throw new InvalidDataException("unsupported build pragma combination");
        if (c.ReadForeignKeys != "OFF" || c.ReadAutomaticIndex != "OFF")
            throw new InvalidDataException("read pragmas must be foreign_keys=OFF, automatic_index=OFF");
        if (c.AnalyzePolicy != "none" || c.VacuumPolicy != "none" || c.OptimizePolicy != "none")
            throw new InvalidDataException("analyze/vacuum/optimize must be none in the baseline");
        if (c.Tables.Select(t => t.Name).Distinct().Count() != 4)
            throw new InvalidDataException("candidate config must define exactly four tables");
        foreach (var t in c.Tables)
        {
            if (!t.Strict) throw new InvalidDataException($"table {t.Name} must be STRICT");
            if (t.Organization is not ("rowid" or "rowid-primary-key"))
                throw new InvalidDataException($"unsupported organization for {t.Name}");
            if (t.Name == "concept" && t.Organization != "rowid-primary-key") throw new InvalidDataException("concept must use rowid-primary-key");
            if (t.Name != "concept" && t.Organization != "rowid") throw new InvalidDataException($"{t.Name} must be plain rowid");
            if (t.Name != "concept" && t.Columns.Any(col => col.PrimaryKey)) throw new InvalidDataException($"{t.Name} must have no PRIMARY KEY/UNIQUE");
        }
        if (c.Indexes.Select(i => i.Name).Distinct().Count() != c.Indexes.Count) throw new InvalidDataException("duplicate candidate index name");
        if (!StructuralEqualsTables(c.Tables, DefaultTables()))
            throw new InvalidDataException("candidate config tables differ from the frozen baseline (name/order/organization/strict/columns)");
        if (!StructuralEqualsIndexes(c.Indexes, DefaultIndexes()))
            throw new InvalidDataException("candidate config indexes differ from the frozen baseline (name/table/column order)");
    }

    private static bool StructuralEqualsTables(IReadOnlyList<TableDef> a, IReadOnlyList<TableDef> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Name != y.Name || x.Organization != y.Organization || x.Strict != y.Strict) return false;
            if (x.Columns.Count != y.Columns.Count) return false;
            for (int j = 0; j < x.Columns.Count; j++)
            {
                var c1 = x.Columns[j];
                var c2 = y.Columns[j];
                if (c1.Name != c2.Name || c1.Type != c2.Type || c1.NotNull != c2.NotNull
                    || c1.PrimaryKey != c2.PrimaryKey || c1.Collation != c2.Collation) return false;
            }
        }
        return true;
    }

    private static bool StructuralEqualsIndexes(IReadOnlyList<IndexDef> a, IReadOnlyList<IndexDef> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Name != b[i].Name || a[i].Table != b[i].Table) return false;
            if (a[i].Columns.Count != b[i].Columns.Count) return false;
            for (int j = 0; j < a[i].Columns.Count; j++)
                if (a[i].Columns[j] != b[i].Columns[j]) return false;
        }
        return true;
    }

    public byte[] ToCanonicalBytes()
    {
        var b = new Canon.Builder();
        b.AddString(Schema).AddLong(SchemaVer);
        b.AddString(ProviderNameValue).AddString(ProviderVersionValue);
        b.AddString(CorpusIdValue).AddString(WorkloadIdValue);
        b.AddLong(BuildPageSize).AddString(BuildJournalMode).AddString(BuildSynchronous);
        b.AddString(BuildForeignKeys).AddString(BuildAutomaticIndex).AddString(BuildTempStore);
        b.AddString(ReadForeignKeys).AddString(ReadAutomaticIndex);
        b.AddString(AnalyzePolicy).AddString(VacuumPolicy).AddString(OptimizePolicy);
        foreach (var t in Tables)
        {
            b.AddString(t.Name).AddString(t.Organization).AddByte(t.Strict ? (byte)1 : (byte)0);
            foreach (var col in t.Columns)
                b.AddString(col.Name).AddString(col.Type).AddByte(col.NotNull ? (byte)1 : (byte)0)
                    .AddByte(col.PrimaryKey ? (byte)1 : (byte)0).AddString(col.Collation ?? string.Empty);
        }
        foreach (var i in Indexes)
        {
            b.AddString(i.Name).AddString(i.Table);
            foreach (var col in i.Columns) b.AddString(col);
        }
        return b.ToArray();
    }

    public string ConfigId() => Canon.Sha256Hex(ToCanonicalBytes());
}

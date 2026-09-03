using System.Text.Json;
using Mimir.Catalog.Corpus;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.CorpusCli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return Usage();

        try
        {
            return args[0] switch
            {
                "passa" => RunPassA(args),
                "passb" => RunPassB(args),
                "fixture" => RunFixture(args),
                "cid" => Cid(args),
                "inspect" => RunInspect(args),
                "validate" => RunValidate(args),
                "gen-workload" => RunGenWorkload(args),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int RunPassB(string[] args)
    {
        string source = Get(args, "--source", SourceIdentity.ExpectedPath);
        string corpus = Get(args, "--corpus", Path.Combine("data", "corpus", CorpusIdentity.ComputeId()));
        Console.WriteLine($"Pass B start: source={source} corpus={corpus}");
        var opts = new PassBOptions { SourcePath = source, CorpusRoot = corpus };
        PassBEvidence ev = PassB.Run(opts);
        Console.WriteLine($"concepts={ev.ConceptRows} lexical={ev.LexicalRows} instanceOf={ev.InstanceOfRows} subclassOf={ev.SubclassOfRows}");
        Console.WriteLine($"observedConcepts={ev.ObservedConceptRows} unobservedTail={ev.UnobservedConceptTail}");
        Console.WriteLine($"tiers: T1={ev.T1Concepts} T2={ev.T2Concepts} cap={ev.T1IntersectT2} t2Only={ev.T2Only} union={ev.T1Concepts + ev.T2Only}");
        Console.WriteLine($"t2Seen={ev.T2SeenCount} t2Unseen={ev.T2UnseenCount} wallSec={ev.WallSeconds:F1}");
        Console.WriteLine($"published -> {ev.PublishedDir}");
        Console.WriteLine($"materialization -> {ev.MaterializationPath}");
        return 0;
    }

    private static int RunInspect(string[] args)
    {
        string corpus = Get(args, "--corpus", Path.Combine("data", "corpus", CorpusIdentity.ComputeId()));
        string passB = Path.Combine(corpus, "pass-b");
        var files = new (string Relation, string File)[]{
            ("concept", "concept.parquet"),
            ("lexical_entry", "lexical_entry.parquet"),
            ("instance_of", "instance_of.parquet"),
            ("subclass_of", "subclass_of.parquet"),
        };

        string matPath = Path.Combine(passB, "materialization.json");
        if (!File.Exists(matPath)) { Console.Error.WriteLine($"missing {matPath}"); return 1; }
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(matPath));
        var artifacts = doc.RootElement.GetProperty("artifacts");

        bool allOk = true;
        foreach (var (relation, file) in files)
        {
            string path = Path.Combine(passB, file);
            bool fileExists = File.Exists(path);
            Mimir.Catalog.Corpus.ParquetInspection.Result? insp = fileExists
                ? Mimir.Catalog.Corpus.ParquetInspection.Inspect(path)
                : null;
            string schema = "missing";
            long rows = -1;
            if (insp != null)
            {
                var expected = relation switch
                {
                    "concept" => Mimir.Catalog.Corpus.PassBSchema.Concept,
                    "lexical_entry" => Mimir.Catalog.Corpus.PassBSchema.LexicalEntry,
                    _ => Mimir.Catalog.Corpus.PassBSchema.Edge,
                };
                var cols = insp.Columns;
                var exp = Mimir.Catalog.Corpus.ParquetInspection.ColumnsOf(expected);
                schema = cols.Count == exp.Count && cols.Zip(exp).All(p => p.First == p.Second) ? "match" : "MISMATCH";
                rows = insp.RowCount;
            }
            var art = artifacts.GetProperty(relation);
            long artRows = art.GetProperty("rowCount").GetInt64();
            long artSize = art.GetProperty("byteSize").GetInt64();
            long artRg = art.GetProperty("rowGroupCount").GetInt64();
            string artSha = art.GetProperty("sha256").GetString()!;
            long actualSize = fileExists ? new FileInfo(path).Length : -1;
            string actualSha = fileExists ? Mimir.Catalog.Corpus.PassB.Sha256OfFile(path) : "n/a";
            int actualRg = insp?.RowGroupCount ?? -1;
            bool schemaOk = schema == "match";
            bool sizeOk = actualSize == artSize;
            bool shaOk = actualSha == artSha;
            bool rowsOk = rows == artRows;
            bool rgOk = actualRg == artRg;
            bool fileOk = fileExists && schemaOk && sizeOk && shaOk && rowsOk && rgOk;
            allOk &= fileOk;
            Console.WriteLine($"{relation}: exists={fileExists} schema={schema} rows={rows} (evidence {artRows}) " +
                              $"rowGroups={actualRg} (evidence {artRg}) size={actualSize} (evidence {artSize}) shaMatch={shaOk}");
        }

        string statePath = Path.Combine(passB, "pass-b.state.json");
        bool complete = false;
        if (File.Exists(statePath))
        {
            using var stateDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
            complete = stateDoc.RootElement.TryGetProperty("state", out var s) && s.GetString() == "Complete";
        }
        Console.WriteLine($"pass-b.state.json state=Complete: {complete}");
        allOk &= complete;
        Console.WriteLine(allOk ? "INSPECT PASS" : "INSPECT FAIL");
        return allOk ? 0 : 1;
    }

    private static int RunValidate(string[] args)
    {
        string corpus = Get(args, "--corpus", Path.Combine("data", "corpus", CorpusIdentity.ComputeId()));
        string fixture = Get(args, "--fixture", Path.Combine("validation", "phase0-anchors-v1.json"));
        Console.WriteLine($"validation start: corpus={corpus} fixture={fixture}");
        string verdict = CorpusValidation.Run(corpus, fixture, out var evidence);
        Console.WriteLine($"verdict={verdict}");
        Console.WriteLine($"concept rows={evidence.ConceptRows} unique={evidence.UniqueConcepts}");
        Console.WriteLine($"tiers: T1={evidence.T1Concepts} T2={evidence.T2Concepts} cap={evidence.T1IntersectT2} t2Only={evidence.T2OnlyConcepts}");
        Console.WriteLine($"tail={evidence.TailCount} tailHashQualified={evidence.TailHashQualified} qids={string.Join(",", evidence.TailHashQualifiedQids)}");
        Console.WriteLine($"lexical={evidence.LexicalRows} instance={evidence.InstanceOfRows} subclass={evidence.SubclassOfRows}");
        Console.WriteLine($"failedGates={evidence.FailedGates.Count}");
        foreach (var f in evidence.FailedGates) Console.WriteLine($"  FAIL: {f}");
        return verdict == CorpusValidation.GO ? 0 : 1;
    }

    private static int RunGenWorkload(string[] args)
    {
        string corpus = Get(args, "--corpus", Path.Combine("data", "corpus", CorpusIdentity.ComputeId()));
        string fixture = Get(args, "--fixture", Path.Combine("validation", "phase0-anchors-v1.json"));
        string? outRoot = null;
        int oi = Array.IndexOf(args, "--out");
        if (oi >= 0 && oi + 1 < args.Length) outRoot = args[oi + 1];
        string contract = Get(args, "--contract", WorkloadRun.DefaultContractPath);
        Console.WriteLine($"workload generation start: corpus={corpus} fixture={fixture} contract={contract}");
        RunReport report = WorkloadRun.Run(corpus, fixture, outRoot, contract);
        Console.WriteLine($"verdict={report.Verdict}");
        Console.WriteLine($"corpusId={report.CorpusId} workloadId={report.WorkloadId}");
        Console.WriteLine($"contractSha={report.MachineContractSha}");
        Console.WriteLine($"measured serving={report.MeasuredServingCount} g1={report.MeasuredG1Count} g2={report.G2BatchCount}");
        Console.WriteLine($"g1 candidates={report.G1CandidatesConsidered} rejectedGuard={report.G1RejectedGuard} maxVisited={report.G1MaxVisited}");
        Console.WriteLine($"g2 candidates={report.G2CandidatesConsidered} rejectedGuard={report.G2RejectedGuard} accepted={report.G2Accepted} maxVisited={report.G2MaxVisited}");
        Console.WriteLine($"wallSec={report.WallSeconds:F1} managedBytes={report.ManagedBytes}");
        foreach (var pc in report.PoolCardinalities.OrderBy(k => k.Key))
            Console.WriteLine($"  pool {pc.Key} {pc.Value}");
        Console.WriteLine("continuity: " + string.Join(", ", report.Continuity.Select(k => $"{k.Key}={k.Value}")));
        if (report.Verdict == WorkloadRun.Go)
        {
            Console.WriteLine($"published={report.PublishedDir}");
            foreach (var f in report.FileSha256.OrderBy(k => k.Key))
                Console.WriteLine($"  {f.Key} {f.Value}");
            return 0;
        }
        foreach (var r in report.Reasons) Console.WriteLine($"  HOLD: {r}");
        return 2;
    }

    private static int Cid(string[] args)
    {
        Console.WriteLine(CorpusIdentity.ComputeId());
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage:\n" +
            "  passa  --source <path> [--work <dir>] [--skip-sha]\n" +
            "  passb  --source <path> --corpus <corpus-root>\n" +
            "  fixture --source <path>\n" +
            "  cid\n" +
            "  inspect --corpus <corpus-root>\n" +
            "  validate --corpus <corpus-root> [--fixture <path>]\n" +
            "  gen-workload --corpus <corpus-root> [--fixture <path>] [--contract <path>] [--out <benchmark-root>]");
        return 2;
    }

    private static string Get(string[] args, string name, string dflt)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : dflt;
    }

    private static int RunPassA(string[] args)
    {
        string source = Get(args, "--source", SourceIdentity.ExpectedPath);
        string work = Get(args, "--work", Path.Combine("data", "corpus", CorpusIdentity.ComputeId(), "pass-a"));
        bool skipSha = Array.IndexOf(args, "--skip-sha") >= 0;

        Console.WriteLine($"Pass A start: source={source} work={work} skipSha={skipSha}");
        var opts = new PassAOptions { SourcePath = source, WorkDir = work, SkipSha = skipSha };
        PassAEvidence ev = PassA.Run(opts);

        Console.WriteLine($"items={ev.Totals.Items} nonItems={ev.Totals.NonItems} malformed={ev.Totals.Malformed} missing={ev.Totals.MissingOrDeleted}");
        Console.WriteLine($"T1={ev.T1} T2={ev.T2} T1∩T2={ev.T1IntersectT2} T2Only={ev.T2Only} T1∪T2={ev.T1UnionT2}");
        Console.WriteLine($"p279 pairs={ev.Totals.P279Pairs} subjects={ev.P279Subjects} objects={ev.P279Objects}");
        Console.WriteLine($"p31 total={ev.Totals.P31Pairs} withT1subject={ev.P31WithT1}");
        Console.WriteLine($"shaFresh={ev.ShaFreshlyMeasured} sha={ev.MeasuredSha256}");
        Console.WriteLine($"wallSec={ev.WallSeconds:F1} tempPeakBytes={ev.TempDiskPeakBytes} endpointBytes={ev.EndpointFileBytes}");
        Console.WriteLine($"evidence -> {Path.Combine(opts.WorkDir, "evidence.json")}");
        return 0;
    }

    private static int RunFixture(string[] args)
    {
        string source = Get(args, "--source", "/tmp/a-prefix.json.gz");
        Console.WriteLine($"Fixture scan: {source}");
        var result = ScanCore.Scan(source, computeSha: false, expectedLength: null, onItem: null, progress: null);
        var totals = result.Totals;
        var doc = new
        {
            items = totals.Items,
            source_records = totals.SourceRecords,
            non_items = totals.NonItems,
            malformed = totals.Malformed,
            missing_or_deleted = totals.MissingOrDeleted,
            label_en = totals.LabelEnPresent,
            label_nb = totals.LabelNbPresent,
            alias_en_strings = totals.AliasEnStrings,
            alias_nb_strings = totals.AliasNbStrings,
            alias_total = totals.AliasEnStrings + totals.AliasNbStrings,
            p31_pairs = totals.P31Pairs,
            p279_pairs = totals.P279Pairs,
            truncated = result.GzipTruncated,
            elapsed_seconds = Math.Round(result.ElapsedSeconds, 3),
        };
        Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}

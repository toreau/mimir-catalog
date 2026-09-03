using System.Text.Json;
using Mimir.Catalog.Corpus;

namespace Mimir.Catalog.CorpusCli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: passa --source <path> --work <dir> [--skip-sha] | fixture --source <path>");
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "passa" => RunPassA(args),
                "fixture" => RunFixture(args),
                "cid" => Cid(args),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int Cid(string[] args)
    {
        Console.WriteLine(CorpusIdentity.ComputeId());
        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: passa --source <path> --work <dir> [--skip-sha] | fixture --source <path>");
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

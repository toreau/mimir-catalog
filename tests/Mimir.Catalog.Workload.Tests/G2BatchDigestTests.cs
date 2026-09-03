using System.Text.Json;
using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Workload.Tests;

public class G2BatchDigestTests
{
    [Fact]
    public void G2BatchDigest_MatchesFrozenGeneratorEncoding()
    {
        var w = SyntheticWorld.Build();
        try
        {
            var r = WorkloadBuild.Build(WorkloadContractV1.Default(), "synth-1",
                w.Concept, w.Lexical, w.Instance, w.Subclass, () => w.Rows, w.FixturePath);
            Assert.Equal(WorkloadBuild.Go, r.Verdict);

            // Parse batch concepts (frozen positional order) and batch expected digest.
            var concepts = new List<long>();
            string? batchDigest = null;
            foreach (var line in System.Text.Encoding.UTF8.GetString(r.GraphLines!).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var e = JsonDocument.Parse(line);
                if (e.RootElement.GetProperty("op").GetString() == "G2")
                    foreach (var c in e.RootElement.GetProperty("concepts").EnumerateArray())
                        concepts.Add(c.GetProperty("qid").GetInt64());
            }
            foreach (var line in System.Text.Encoding.UTF8.GetString(r.ExpectedLines!).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var e = JsonDocument.Parse(line);
                var x = e.RootElement;
                if (x.GetProperty("op").GetString() == "G2" && x.GetProperty("kind").GetString() == "Batch")
                    batchDigest = x.GetProperty("digest").GetString();
            }
            Assert.Equal(200, concepts.Count);
            Assert.NotNull(batchDigest);

            // Recompute structural sets exactly like the generator.
            long[]? parents(long q) => w.Subclass.TryGetTargets(q, out var ts) ? ts : Array.Empty<long>();
            var rows = new List<(long Qid, long[] Structural)>();
            foreach (long q in concepts)
            {
                var set = new SortedSet<long>();
                if (w.Instance.TryGetTargets(q, out var targets))
                {
                    foreach (long tg in targets)
                    {
                        set.Add(tg);
                        var trav = GraphTraversal.Ancestry(tg, 3, 5000, parents);
                        foreach (long a in trav.Discovered) set.Add(a);
                    }
                }
                rows.Add((q, set.ToArray()));
            }

            Assert.Equal(batchDigest, WorkloadOracle.G2BatchDigest(rows));
            Assert.Equal(batchDigest, WorkloadOracle.G2BatchDigest(rows)); // deterministic
        }
        finally { SyntheticWorld.Cleanup(w); }
    }
}

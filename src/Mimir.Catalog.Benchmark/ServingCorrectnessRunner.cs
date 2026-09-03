using Mimir.Catalog.Workload;

namespace Mimir.Catalog.Benchmark;

/// <summary>
/// Candidate-neutral S1-S5 correctness runner. Dispatch/materialize is separated
/// from canonicalize/digest/compare so later timing code can wrap only the
/// storage retrieval without redesign.
/// </summary>
public sealed class ServingCorrectnessRunner
{
    private readonly IStorageCandidate _candidate;

    public ServingCorrectnessRunner(IStorageCandidate candidate) => _candidate = candidate;

    public IReadOnlyList<ProbeResult> RunAll(ServingWorkload workload)
    {
        var results = new List<ProbeResult>(workload.Probes.Count);
        foreach (var probe in workload.Probes)
            results.Add(RunProbe(probe, workload.Expected[(probe.Op, probe.Seq)]));
        return results;
    }

    public ProbeResult RunProbe(ServingProbe probe, ServingExpected expected)
    {
        try
        {
            var (cardinality, digest) = MaterializeAndCanonicalize(probe);
            bool valid = cardinality == expected.Cardinality && digest == expected.Digest;
            return new ProbeResult
            {
                Op = probe.Op,
                Seq = probe.Seq,
                Stratum = probe.Stratum,
                Measured = probe.Measured,
                Status = valid ? ServingStatuses.Valid : ServingStatuses.Invalid,
                ExpectedCardinality = expected.Cardinality,
                ActualCardinality = cardinality,
                ExpectedDigest = expected.Digest,
                ActualDigest = digest,
            };
        }
        catch (Exception ex)
        {
            return new ProbeResult
            {
                Op = probe.Op,
                Seq = probe.Seq,
                Stratum = probe.Stratum,
                Measured = probe.Measured,
                Status = ServingStatuses.Error,
                ExpectedCardinality = expected.Cardinality,
                ErrorMessage = ex.Message,
            };
        }
    }

    private (long Cardinality, string Digest) MaterializeAndCanonicalize(ServingProbe probe)
    {
        switch (probe.Op)
        {
            case "S1":
            {
                var hit = _candidate.GetConcept(probe.Qid!.Value);
                string digest = WorkloadOracle.ConceptResultDigest(probe.Qid.Value, hit.Present, hit.InT1, hit.InT2);
                return (hit.Present ? 1 : 0, digest);
            }
            case "S2":
            {
                var members = _candidate.LookupLexical(probe.Lang!, probe.Value!);
                bool isMiss = probe.Stratum == "Miss";
                string digest = members.Count == 0 && isMiss
                    ? WorkloadOracle.LexMissDigest(probe.Lang!, probe.Value!)
                    : WorkloadOracle.LexMembersDigest(members.Select(m => (m.Qid, m.LexKind)).ToList());
                return (members.Count, digest);
            }
            case "S3":
            {
                var rows = _candidate.GetLexicalByQid(probe.Qid!.Value);
                if (rows.Any(r => r.Qid != probe.Qid.Value))
                    return (rows.Count, "<invalid-qid>");
                string digest = WorkloadOracle.LexicalRowsDigest(
                    probe.Qid.Value,
                    rows.Select(r => (r.Lang, r.LexKind, r.Value)).ToList());
                return (rows.Count, digest);
            }
            case "S4":
            case "S5":
            {
                var targets = probe.Op == "S4"
                    ? _candidate.GetInstanceOf(probe.Qid!.Value)
                    : _candidate.GetSubclassOf(probe.Qid!.Value);
                var sorted = targets.OrderBy(t => t).ToArray();
                return (sorted.Length, WorkloadOracle.AdjacencyDigest(sorted));
            }
            default:
                throw new InvalidOperationException($"unsupported serving op {probe.Op}");
        }
    }
}

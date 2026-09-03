# Mimir Catalog

Phase 1 of Mimir: an evidence-driven baseline catalog architecture investigation.
This repository is intentionally minimal; it contains no storage-engine candidate
and no benchmark workload execution.

Phase 0 (the qualified bootstrap source contract and evidence) is authoritative in
`~/src/aursand.no/mimir.aursand.no`; there is no runtime dependency on that Python
repository from here.

## Current slice

Phase 1.1A.2a: corpus-builder foundation and the single full-source Pass A.

- `Mimir.Catalog.Corpus`: frozen corpus contract, deterministic T1 hash, strict
  Wikidata parser with Phase-0 semantic equivalence, Pass-A scan and evidence.
- `Mimir.Catalog.CorpusCli`: CLI. Temp SQLite is used only as a Pass-A
  aggregation implementation detail; it is not a production storage decision.
- `Mimir.Catalog.Corpus.Tests`: deterministic local tests (no full source scan).

## Commands

```text
dotnet test tests/Mimir.Catalog.Corpus.Tests

# bounded parser-equivalence gate against the Phase-0 fixture
dotnet run --project src/Mimir.Catalog.CorpusCli -c Release -- fixture --source /tmp/a-prefix.json.gz

# full Pass A (long-running)
dotnet run --project src/Mimir.Catalog.CorpusCli -c Release -- passa \
  --source /tmp/wikidata-latest-all.json.gz.partial \
  --work data/corpus/<corpus-id>/pass-a
```

Pass-A output (evidence.json, t2-endpoints.bin, probe-hints, state.json) lives
under `data/` which is gitignored. See `docs/phase1/`.

# Mimir Catalog

Phase 1 of Mimir: an evidence-driven baseline catalog architecture investigation.
This repository is intentionally minimal; it contains no storage-engine candidate
and no benchmark workload execution.

Phase 0 (the qualified bootstrap source contract and evidence) is authoritative in
`~/src/aursand.no/mimir.aursand.no`; there is no runtime dependency on that Python
repository from here.

## Current state

- **Pass A (accepted, committed `5f3cc58`)**: corpus-builder foundation and the
  single full-source Pass-A structural evidence run.
- **Pass B (accepted, committed `cb01e33`)**: materializes the frozen benchmark
  corpus (`T1 ∪ T2`) from the pinned source into relation-split Parquet.
- **Corpus validation (accepted, committed `fd6e080`)**: read-only validation
  (`validate`) of the published Pass-B corpus plus Phase-0 anchor continuity;
  verdict GO on the current corpus.
- **Workload/metrics contract (accepted)**:
  engine-neutral workload contract (`docs/phase1/1.1a.3-workload-contract.md`,
  `benchmarks/workload-contract-v1.json`) and deterministic generator
  (`gen-workload`). Authoritative workload-v1 generation is GO and
  published (`data/benchmarks/<corpus-id>/workload-v1`); S2 Fanout51Plus is a
  380-key census (`fanout >= 51`). Evidence: `docs/phase1/1.1a.3-closeout.md`.
  Storage candidates have not started.
- The relation-split Parquet is **benchmark interchange only**. It is not a
  production-storage verdict; SQLite/DuckDB/Parquet-direct/hybrid storage
  candidates have not started.

## Layout

- `Mimir.Catalog.Corpus`: frozen corpus contract, deterministic T1 hash, strict
  Wikidata parser with Phase-0 semantic equivalence, Pass-A/Pass-B implementation.
- `Mimir.Catalog.CorpusCli`: CLI.
- `Mimir.Catalog.Corpus.Tests`: deterministic local tests (no full source scan).

## Commands

```text
dotnet test tests/Mimir.Catalog.Corpus.Tests

# bounded parser-equivalence gate against the Phase-0 fixture
dotnet run --project src/Mimir.Catalog.CorpusCli -c Release -- fixture --source /tmp/a-prefix.json.gz

# full Pass A (structural evidence)
dotnet run --project src/Mimir.Catalog.CorpusCli -c Release -- passa \
  --source /tmp/wikidata-latest-all.json.gz.partial --work data/corpus/<corpus-id>/pass-a

# full Pass B (corpus materialization)
dotnet run --project src/Mimir.Catalog.CorpusCli -c Release -- passb \
  --source /tmp/wikidata-latest-all.json.gz.partial --corpus data/corpus/<corpus-id>

# read-only inspection/validation of a published Pass-B directory
dotnet run --project src/Mimir.Catalog.CorpusCli -c Release -- inspect --corpus data/corpus/<corpus-id>

# corpus validation + representativeness closeout (uses tracked Phase-0 anchor fixture)
dotnet run --project src/Mimir.Catalog.CorpusCli -c Release -- validate --corpus data/corpus/<corpus-id>

# workload/metrics contract: deterministic authoritative probe generation
dotnet run --project src/Mimir.Catalog.CorpusCli -c Release -- gen-workload \
  --corpus data/corpus/<corpus-id> [--fixture validation/phase0-anchors-v1.json]
```

Pass-A/Pass-B output lives under `data/` which is gitignored. See `docs/phase1/`.

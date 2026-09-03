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
- **Pass B (implemented, uncommitted)**: materializes the frozen benchmark corpus
  (`T1 ∪ T2`) from the pinned source into relation-split Parquet.
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
```

Pass-A/Pass-B output lives under `data/` which is gitignored. See `docs/phase1/`.

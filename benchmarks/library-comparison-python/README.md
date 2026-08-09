# Library comparison, Stage 2 — the Python entrants

Phase 3.14's Python rows: **LangChain, LlamaIndex and Haystack, each at its own defaults**, on
the same BEIR corpora and the same pinned `all-MiniLM-L6-v2` as every .NET entrant. Each run
emits a **TREC run file and nothing else** — no Python code computes a metric; every figure is
computed by the one `IrMetrics` behind this repository's published BEIR numbers, via
`BeirPythonEntrantsTests` in `tests/Rag.NET.Benchmarks.Quality.IntegrationTests`.

The defaults each entrant runs at are recorded, with source citations at the pinned versions, in
[`docs/reference/library-comparison-defaults.md`](../../docs/reference/library-comparison-defaults.md)
(Stage 2 section) — written **before** the entrants, so the entrants match the page rather than
the page excusing the entrants.

## Reproducing

Requirements: [`uv`](https://docs.astral.sh/uv/) (the lockfile pins CPython 3.14.5 and every
package), plus the same environment variables the .NET BEIR measurements take:

- `RAGNET_BEIR_CACHE` — directory holding the extracted BEIR datasets (`scifact/`, `arguana/`);
  run files are written to its `runs/` subdirectory and the Python-side vector cache to
  `embeddings-python/`
- `RAGNET_ONNX_EMBED_MODEL` / `RAGNET_ONNX_EMBED_VOCAB` — the pinned `all-MiniLM-L6-v2` ONNX
  export (token-level output) and its WordPiece `vocab.txt`, revision and SHA-256 pinned in
  `.github/workflows/nightly.yml`

```
uv sync
uv run python identity_check.py --write-battery %RAGNET_BEIR_CACHE%\identity-battery
rem the .NET half of the battery, from the repository root:
rem   set RAGNET_IDENTITY_BATTERY_DIR=%RAGNET_BEIR_CACHE%\identity-battery
rem   dotnet test tests\Rag.NET.Benchmarks.Quality.IntegrationTests ^
rem     --filter "DisplayName~DumpsEachBatteryInputsVector"
uv run python identity_check.py %RAGNET_BEIR_CACHE%\identity-battery   # all six must be OK
uv run python run_entrant.py scifact langchain              # then produce run files
uv run python run_entrant.py arguana llamaindex             # etc.
uv run python run_entrant.py fiqa haystack --warm-cache     # first run on a cold vector cache
```

`run_entrant.py` is runnable from **any** working directory, this one included. That took
deliberate work: nltk 3.10.1, which LlamaIndex's `SentenceSplitter` imports, ships a security
shim (`nltk/inisec.py`) that refuses any nltk-initiated import resolving under the current
working directory — and `.venv/` lives under this directory, so any cwd that is an ancestor of
`.venv/` (this directory, or the repository root) would make nltk block its own dependencies
(`regex`). The script therefore puts its own directory on `sys.path`, absolutises the `RAGNET_*`
environment variables, and moves itself to a neutral cwd before any library is imported —
nothing it does means anything by its working directory.

**Timed spans never read the vector cache from disk.** Each run makes an untimed prefetch pass
first — a rehearsal `entrant.build` discovers the exact chunk texts the library will embed, and
the judged query texts are prefetched directly — reading every needed vector into memory; the
timed passes are then served from that memory only. The indexing figure is the library building
an index from vectors it already has, **not "the cost of indexing"**: with one cache-file read
per text inside the span, identical runs differed by up to 23x on OS page-cache state alone
(55.2 s cold vs 2.4 s hot, SciFact/LangChain, same code and corpus). A text the cache does not
hold is a **cold cache** and fails loudly, naming the count — pass `--warm-cache` to let the
prefetch pass embed and store the misses, still outside every timed span. The timings sidecar's
hit/miss counts always describe the prefetch pass, i.e. what the disk really held.

`identity_check.py` must pass before any entrant row is trusted: it compares the Python-side
embedder against vectors `OnnxEmbeddingGenerator` itself produced, over a six-string battery
chosen to cover every step where the two pipelines could diverge (its docstring says why a
corpus-wide sweep would only re-demonstrate the same equality at a thousand times the cost). The
.NET half of the battery is `IdentityBatteryDumpTests` in
`tests/Rag.NET.Benchmarks.Quality.IntegrationTests` — opt-in via `RAGNET_IDENTITY_BATTERY_DIR`,
as commented in the block above. If the vectors differ, every Python row is measuring a different
model and the stage is invalid.

Nothing in `RAGNET_BEIR_CACHE` is ever committed: corpora, models, vectors and run files are all
derived or third-party data.

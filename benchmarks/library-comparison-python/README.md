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
uv run python identity_check.py [known-vector-dotnet.txt]   # prove the embedder first
uv run python run_entrant.py scifact langchain              # then produce run files
uv run python run_entrant.py arguana llamaindex             # etc.
```

**Run the entrants from a working directory that is not this project directory** (e.g.
`cd %RAGNET_BEIR_CACHE% && set PYTHONPATH=<this dir> && uv run --project <this dir> python
<this dir>/run_entrant.py …`): nltk 3.10.1, which LlamaIndex's `SentenceSplitter` imports, ships a
security shim (`nltk/inisec.py`) that refuses any nltk-initiated import resolving under the
current working directory — and `.venv/` lives under this directory, so from here it blocks its
own dependencies.

`identity_check.py` must pass before any entrant row is trusted: it compares the Python-side
embedder against vectors `OnnxEmbeddingGenerator` itself produced (a known-string dump, plus
every SciFact/ArguAna corpus and judged-query text via the .NET embedding cache). If the vectors
differ, every Python row is measuring a different model and the stage is invalid.

Nothing in `RAGNET_BEIR_CACHE` is ever committed: corpora, models, vectors and run files are all
derived or third-party data.

"""Runs one Python entrant over one BEIR dataset and writes its TREC run file.

Usage::

    uv run python run_entrant.py <scifact|arguana|fiqa> <langchain|llamaindex|haystack>

Environment: ``RAGNET_BEIR_CACHE`` (extracted datasets, and where ``runs/`` and the Python
vector cache live), ``RAGNET_ONNX_EMBED_MODEL`` and ``RAGNET_ONNX_EMBED_VOCAB`` (the pinned
``all-MiniLM-L6-v2`` export and its WordPiece vocabulary).

The protocol is the .NET harness's, exactly:

- **judged queries only** (``BeirHarness.JudgedQueries``): unjudged queries cannot be scored and
  would bill every per-query resource for rankings that are thrown away;
- **retrieval depth over-shoots the cutoff** by the corpus's max-units-per-document factor, plus
  one more cutoff's worth when the dataset excludes the query's own document
  (``BeirHarness.RetrieveAsync``) -- otherwise pooling is handed a list top-k already truncated;
- **self-exclusion and max-pooling happen here, on the writer's side of the boundary**, before
  the file exists (``doc_ranking.top_documents``), so the published run file already holds the
  post-exclusion top 10 and an outsider's trec_eval scores what IrMetrics scores;
- the run file's tag names the library and the exact version measured.

After writing, the run file's own bytes are re-read and checked: zero lines pair a query with the
document sharing its id (on ArguAna, 1,298 of 1,406 queries are byte-identical to their own
corpus document, so zero is not achievable by accident). **Nothing here computes a metric** --
scoring happens on the .NET side, through ``TrecRunFile.Read`` and ``IrMetrics``.
"""

from __future__ import annotations

import os
import sys
import time
from pathlib import Path

import entrant_haystack
import entrant_langchain
import entrant_llamaindex
from beir_data import load_dataset
from doc_ranking import top_documents
from pinned_embedder import PYTHON_MODEL_IDENTITY, PinnedEmbedder
from trec_run import write
from vector_cache import VectorCache

CUTOFF = 10  # BeirHarness.Cutoff: the rank cutoff the published figures are quoted at

ENTRANTS = {
    module.NAME: module
    for module in (entrant_langchain, entrant_llamaindex, entrant_haystack)
}


def main() -> int:
    if len(sys.argv) != 3 or sys.argv[1] not in ("scifact", "arguana", "fiqa") \
            or sys.argv[2] not in ENTRANTS:
        print(__doc__)
        return 2

    dataset_name, entrant_name = sys.argv[1], sys.argv[2]
    cache_directory = _required_env("RAGNET_BEIR_CACHE")
    model_path = _required_env("RAGNET_ONNX_EMBED_MODEL")
    vocab_path = _required_env("RAGNET_ONNX_EMBED_VOCAB")

    entrant = ENTRANTS[entrant_name]
    dataset = load_dataset(cache_directory, dataset_name)
    descriptor = dataset.descriptor

    embedder = PinnedEmbedder(model_path, vocab_path)
    cache = VectorCache(
        Path(cache_directory) / "embeddings-python", PYTHON_MODEL_IDENTITY)

    def embed_many(texts: list[str]):
        return cache.get_or_embed(texts, embedder.embed)

    started = time.monotonic()
    retrieve, max_units_per_document, unit_count = entrant.build(dataset.documents, embed_many)

    # BeirHarness.RetrieveAsync's TopK rule, verbatim.
    excludes_self = descriptor.excludes_self_retrieved_document
    depth = (CUTOFF + (1 if excludes_self else 0)) * max_units_per_document

    runs: dict[str, list[tuple[str, float]]] = {}
    for query in dataset.judged_queries():
        hits = retrieve(query.text, depth)
        excluded = query.id if excludes_self else None
        runs[query.id] = top_documents(hits, CUTOFF, excluded)

    runs_directory = Path(cache_directory) / "runs"
    runs_directory.mkdir(parents=True, exist_ok=True)
    run_file = runs_directory / f"{dataset_name}-{entrant_name}.trec"
    write(run_file, runs, entrant.RUN_TAG)

    self_lines = _verify_no_self_lines(run_file)
    elapsed = time.monotonic() - started

    print(f"{dataset_name} {entrant.NAME.upper()} AT ITS DEFAULTS ({entrant.RUN_TAG})")
    print(f"  {entrant.DESCRIPTION}")
    print(f"  {len(dataset.documents)} documents -> {unit_count} units "
          f"(max {max_units_per_document} from one document), retrieval depth {depth}")
    print(f"  {len(runs)} judged queries retrieved; self-exclusion check: "
          f"{self_lines} query-id = document-id lines "
          f"({'protocol requires 0' if excludes_self else 'dataset does not self-exclude'})")
    print(f"  cache: {cache.hits} hits, {cache.misses} misses; elapsed {elapsed:.1f} s")
    print(f"  run file: {run_file}")

    if excludes_self and self_lines != 0:
        print("SELF-EXCLUSION FAILED: the run file holds pre-exclusion rankings.")
        return 1
    return 0


def _verify_no_self_lines(run_file: Path) -> int:
    """Counts lines whose query id equals its document id, on the file's own bytes."""
    self_lines = 0
    with open(run_file, encoding="utf-8") as file:
        for line in file:
            fields = line.split()
            if len(fields) == 6 and fields[0] == fields[2]:
                self_lines += 1
    return self_lines


def _required_env(name: str) -> str:
    value = os.environ.get(name, "")
    if not value:
        raise SystemExit(
            f"{name} is not set. Set RAGNET_BEIR_CACHE, RAGNET_ONNX_EMBED_MODEL and "
            "RAGNET_ONNX_EMBED_VOCAB the way the .NET BEIR measurements take them.")
    return value


if __name__ == "__main__":
    raise SystemExit(main())

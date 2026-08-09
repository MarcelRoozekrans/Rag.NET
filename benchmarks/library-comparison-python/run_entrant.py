"""Runs one Python entrant over one BEIR dataset and writes its TREC run file.

Usage::

    uv run python run_entrant.py <scifact|arguana|fiqa> <langchain|llamaindex|haystack> \\
        [--warm-cache] [--run-index N]

Runnable from **any** working directory, this one included -- the bootstrap below moves the
process to a neutral cwd before anything imports nltk. ``--warm-cache`` permits the untimed
prefetch pass to embed and store texts the vector cache does not hold yet; without it a cold
cache fails loudly instead of quietly paying model and disk costs no other run paid.
``--run-index N`` (default 1) names which repeat run this is: the sidecar lands at
``timings.path_for(run_file, N)`` so repeats accumulate instead of overwriting -- no cost figure
may be published from a single run, and the .NET side's ``CostReproducibility`` gate compares
the repeats and publishes their spread.

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

The entrant also **times itself, in its own runtime** (``timings.py`` writes the sidecar beside
the run file): ``time.perf_counter()`` around ``entrant.build`` only for indexing, and around the
``retrieve`` call only per query -- never around ``top_documents``, which is harness protocol
identical across entrants, and never around a process. The elapsed line below is derived from
those spans rather than measured separately, so the print and the sidecar cannot drift apart.

**Every vector a timed span needs is prefetched into memory first** -- a rehearsal
``entrant.build`` discovers the exact chunk texts the library will embed, the judged query texts
are prefetched directly, and only then do the timed passes run, served from memory
(``VectorCache.prefetch`` / ``serve``). The indexing figure is therefore **not "the cost of
indexing"**: it is the library building an index from vectors it already has, with embedding and
its disk I/O excluded by construction. Before this, one cache-file read per text sat inside the
span, and identical runs differed by up to 23x on OS page-cache state alone (55.2 s cold vs
2.4 s hot on SciFact/LangChain) -- a figure about run order, not about any library. The sidecar's
hit/miss counts describe the prefetch pass, i.e. what the disk really held.

**A known bias the rehearsal introduces, stated rather than left implicit:** the timed build is
the *second* ``entrant.build`` in this process -- warmed by the rehearsal -- while the .NET rows
need no rehearsal and time a first build, so the bias pushes every Python indexing figure down
relative to .NET's. It is acceptable because all three Python entrants get identical treatment
and indexing publishes per ecosystem, never cross-ecosystem (the roadmap's §6 decision); the
rehearsal itself cannot go, because the chunk texts to prefetch are library-specific and
unknowable up front.
"""

from __future__ import annotations

import os
import sys
import tempfile
from pathlib import Path

# ---------------------------------------------------------------------------------------------
# Import bootstrap, BEFORE the entrant imports below pull in the libraries.
#
# nltk >= 3.10 (imported by LlamaIndex's SentenceSplitter) installs a guard (nltk/inisec.py,
# a CWE-427 mitigation) that blocks any nltk-initiated import resolving under the current
# working directory. This project's ``.venv`` lives under this directory, so any cwd that is an
# ancestor of ``.venv`` -- this directory, or the repository root -- makes nltk block its own
# dependencies (``regex``). Nothing in this script means anything by its cwd (every path comes
# from an environment variable, absolutised here first), so move to a neutral one before any
# library is imported. The sys.path insert keeps the sibling modules importable when the
# interpreter was started with ``-P``/``PYTHONSAFEPATH``, which drop the script directory.
# ---------------------------------------------------------------------------------------------
_SCRIPT_DIRECTORY = Path(__file__).resolve().parent
if str(_SCRIPT_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(_SCRIPT_DIRECTORY))
for _variable in ("RAGNET_BEIR_CACHE", "RAGNET_ONNX_EMBED_MODEL", "RAGNET_ONNX_EMBED_VOCAB"):
    _value = os.environ.get(_variable)
    if _value:
        os.environ[_variable] = str(Path(_value).resolve())
os.chdir(tempfile.gettempdir())

import time  # noqa: E402

import entrant_haystack  # noqa: E402
import entrant_langchain  # noqa: E402
import entrant_llamaindex  # noqa: E402
import timings  # noqa: E402
from beir_data import load_dataset  # noqa: E402
from doc_ranking import top_documents  # noqa: E402
from pinned_embedder import PYTHON_MODEL_IDENTITY, PinnedEmbedder  # noqa: E402
from trec_run import write  # noqa: E402
from vector_cache import VectorCache  # noqa: E402

CUTOFF = 10  # BeirHarness.Cutoff: the rank cutoff the published figures are quoted at

ENTRANTS = {
    module.NAME: module
    for module in (entrant_langchain, entrant_llamaindex, entrant_haystack)
}


def main() -> int:
    arguments = [argument for argument in sys.argv[1:] if argument != "--warm-cache"]
    warm_cache = len(arguments) != len(sys.argv) - 1
    arguments, run_index = _take_run_index(arguments)
    if run_index < 1 or len(arguments) != 2 \
            or arguments[0] not in ("scifact", "arguana", "fiqa") \
            or arguments[1] not in ENTRANTS:
        print(__doc__)
        return 2

    dataset_name, entrant_name = arguments
    cache_directory = _required_env("RAGNET_BEIR_CACHE")
    model_path = _required_env("RAGNET_ONNX_EMBED_MODEL")
    vocab_path = _required_env("RAGNET_ONNX_EMBED_VOCAB")

    entrant = ENTRANTS[entrant_name]
    dataset = load_dataset(cache_directory, dataset_name)
    descriptor = dataset.descriptor
    judged = dataset.judged_queries()

    embedder = PinnedEmbedder(model_path, vocab_path)
    cache = VectorCache(
        Path(cache_directory) / "embeddings-python", PYTHON_MODEL_IDENTITY)

    # The prefetch pass, untimed: a rehearsal build discovers the exact chunk texts this
    # library will embed and reads their vectors into memory; the judged query texts are known
    # up front and prefetched directly. All disk I/O the run needs happens here, before any
    # clock starts. A text with no cache entry raises (cold cache) unless --warm-cache was
    # given, in which case it is embedded and stored here -- still outside every span.
    warm_embed = embedder.embed if warm_cache else None
    entrant.build(dataset.documents, lambda texts: cache.prefetch(texts, warm_embed))
    cache.prefetch([query.text for query in judged], warm_embed)

    def embed_many(texts: list[str]):
        return cache.serve(texts)

    # perf_counter, not monotonic: the higher-resolution clock, and per-query spans are small.
    # The span brackets entrant.build ONLY -- dataset loading, embedder construction and the
    # run-file write are the harness's own cost, not the library's. Embedding is served from
    # the prefetched memory map, so what this measures is the library building an index from
    # vectors it already has: NOT "the cost of indexing", by construction.
    indexing_started = time.perf_counter()
    retrieve, max_units_per_document, unit_count = entrant.build(dataset.documents, embed_many)
    indexing_seconds = time.perf_counter() - indexing_started

    # BeirHarness.RetrieveAsync's TopK rule, verbatim.
    excludes_self = descriptor.excludes_self_retrieved_document
    depth = (CUTOFF + (1 if excludes_self else 0)) * max_units_per_document

    runs: dict[str, list[tuple[str, float]]] = {}
    query_latencies_milliseconds: dict[str, float] = {}
    for query in judged:
        # The span brackets the library's retrieval ONLY. top_documents (pooling and the
        # self-exclusion) is harness protocol, deliberately identical across entrants, so timing
        # it would smear the same constant into five different libraries' latency columns. The
        # query embedding inside the span resolves from the prefetched memory map -- the span
        # holds the library's search, with no disk read to inherit the page cache's state.
        query_started = time.perf_counter()
        hits = retrieve(query.text, depth)
        query_latencies_milliseconds[query.id] = (time.perf_counter() - query_started) * 1000.0
        excluded = query.id if excludes_self else None
        runs[query.id] = top_documents(hits, CUTOFF, excluded)

    runs_directory = Path(cache_directory) / "runs"
    runs_directory.mkdir(parents=True, exist_ok=True)
    run_file = runs_directory / f"{dataset_name}-{entrant_name}.trec"
    write(run_file, runs, entrant.RUN_TAG)
    timings.write(
        run_file,
        run_tag=entrant.RUN_TAG,
        indexing_seconds=indexing_seconds,
        query_latencies_milliseconds=query_latencies_milliseconds,
        embedding_cache_hits=cache.hits,
        embedding_cache_misses=cache.misses,
        unit_count=unit_count,
        max_units_per_document=max_units_per_document,
        run_index=run_index)

    self_lines = _verify_no_self_lines(run_file)
    # Derived from the measured spans rather than timed separately, so this line and the sidecar
    # cannot drift apart. It deliberately no longer covers loading or file I/O.
    retrieval_seconds = sum(query_latencies_milliseconds.values()) / 1000.0

    print(f"{dataset_name} {entrant.NAME.upper()} AT ITS DEFAULTS ({entrant.RUN_TAG})")
    print(f"  {entrant.DESCRIPTION}")
    print(f"  {len(dataset.documents)} documents -> {unit_count} units "
          f"(max {max_units_per_document} from one document), retrieval depth {depth}")
    print(f"  {len(runs)} judged queries retrieved; self-exclusion check: "
          f"{self_lines} query-id = document-id lines "
          f"({'protocol requires 0' if excludes_self else 'dataset does not self-exclude'})")
    print(f"  cache: {cache.hits} hits, {cache.misses} misses (prefetch pass; timed spans "
          "serve from memory); "
          f"indexing {indexing_seconds:.1f} s + retrieval {retrieval_seconds:.1f} s (self-timed)")
    print(f"  run file: {run_file}")
    print(f"  timings sidecar: {timings.path_for(run_file, run_index)} (run {run_index})")

    if excludes_self and self_lines != 0:
        print("SELF-EXCLUSION FAILED: the run file holds pre-exclusion rankings.")
        return 1
    return 0


def _take_run_index(arguments: list[str]) -> tuple[list[str], int]:
    """Pops ``--run-index N`` off the argument list, defaulting to run 1.

    Returns 0 as the index when the flag is present but its value is missing or not a positive
    integer, so ``main`` prints the usage text instead of half-running with a guessed index.
    """
    if "--run-index" not in arguments:
        return arguments, 1
    position = arguments.index("--run-index")
    value = arguments[position + 1] if position + 1 < len(arguments) else ""
    remaining = arguments[:position] + arguments[position + 2:]
    return remaining, int(value) if value.isdigit() and int(value) >= 1 else 0


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

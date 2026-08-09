"""A timings-sidecar writer that produces files ``TimingsSidecar.Read`` accepts.

``trec_run.py``'s sibling, for the cost half of the boundary: beside every run file the harness
writes one ``<run-file>.timings.json`` holding what the entrant measured about itself -- indexing
wall-clock, one raw latency per judged query, and the cache counts that say what those numbers
mean. **Nothing in Python computes a statistic**: percentiles come out of the one
``LatencyStatistics`` on the .NET side, fed these raw samples, so no library's own idea of "p99"
can reach a table.

This writer enforces the same invariants ``TimingsSidecar.Write`` does, for the same reasons:

- the sidecar path is **derived** from the run file's (``path_for``), never chosen by a caller,
  so a run file and a sidecar cannot be paired by accident;
- query ids in ordinal order, ``\\n`` line ends, fields in the reader's fixed order -- the same
  measurements produce the same file on any machine (Python's ``sorted`` over ``str`` compares
  code points, which is exactly ``StringComparer.Ordinal``);
- numbers via ``repr``, which is invariant-culture by construction (``.`` decimal separator).
  The stakes are higher than in the run file: a locale-formatted ``12,5`` is not a mangled JSON
  number but **two members**, so the failure would surface in whoever read the file next, far
  from whoever wrote it;
- an empty latency map, a negative or non-finite span, a negative count, or a token carrying
  whitespace or a character JSON would have to escape all **raise here**, because every quiet
  default downstream is catastrophic -- a missing latency read as 0 ms is a p50 that reads as a
  win, and a missing miss count read as 0 asserts a warm embedding cache for a cold run.
"""

from __future__ import annotations

import math
from pathlib import Path

FILE_SUFFIX = ".timings.json"


def path_for(run_file: str | Path, run_index: int = 1) -> Path:
    """The sidecar path belonging to one repeat run of a run file.

    Run 1 keeps the historical unindexed name -- ``<run-file>.timings.json`` -- so a single-run
    pairing stays where every existing reader looks; run ``N >= 2`` becomes
    ``<run-file>.timings.N.json``, the same convention extended with an index (exactly as
    ``TimingsSidecar.PathFor`` extends it), never a second format. Repeats exist because no cost
    figure may be published from a single run: the .NET ``CostReproducibility`` gate compares
    them, and without the index every repeat would overwrite the last.
    """
    _require_run_index(run_index)
    if run_index == 1:
        return Path(str(run_file) + FILE_SUFFIX)
    return Path(f"{run_file}.timings.{run_index}.json")


def write(
    run_file: str | Path,
    *,
    run_tag: str,
    indexing_seconds: float,
    query_latencies_milliseconds: dict[str, float],
    embedding_cache_hits: int,
    embedding_cache_misses: int,
    unit_count: int,
    max_units_per_document: int,
    run_index: int = 1,
) -> None:
    """Writes one entrant's self-measured timings beside its run file.

    ``indexing_seconds`` brackets ``entrant.build`` only; each latency brackets the entrant's
    ``retrieve`` call only -- pooling is harness protocol, identical across entrants, and stays
    outside every span. ``run_index`` says which repeat run this is (``path_for`` derives the
    sidecar's name from it), so repeats accumulate for the .NET reproducibility gate instead of
    overwriting each other.
    """
    _require_token(run_tag, "run tag")
    _require_span(indexing_seconds, "indexing wall-clock")
    _require_count(embedding_cache_hits, 0, "embedding-cache hit count")
    _require_count(embedding_cache_misses, 0, "embedding-cache miss count")
    _require_count(unit_count, 1, "unit count")
    _require_count(max_units_per_document, 1, "max units per document")
    if not query_latencies_milliseconds:
        raise ValueError(
            "The per-query latency map is empty. An entrant that timed zero queries did not run "
            "infinitely fast; it lost its query set, and every percentile over nothing is 0/0.")
    for query_id, latency in query_latencies_milliseconds.items():
        _require_token(query_id, "query id")
        _require_span(latency, f"latency for query {query_id!r}")

    lines = [
        "{\n",
        f'  "runTag": "{run_tag}",\n',
        f'  "indexingSeconds": {indexing_seconds!r},\n',
        f'  "embeddingCacheHits": {embedding_cache_hits},\n',
        f'  "embeddingCacheMisses": {embedding_cache_misses},\n',
        f'  "unitCount": {unit_count},\n',
        f'  "maxUnitsPerDocument": {max_units_per_document},\n',
        '  "queryLatenciesMilliseconds": {\n',
    ]
    query_ids = sorted(query_latencies_milliseconds)  # ordinal, as TimingsSidecar.Write sorts
    for position, query_id in enumerate(query_ids, start=1):
        separator = "\n" if position == len(query_ids) else ",\n"
        lines.append(f'    "{query_id}": {query_latencies_milliseconds[query_id]!r}{separator}')
    lines.append("  }\n}\n")

    path_for(run_file, run_index).write_bytes("".join(lines).encode("utf-8"))


def _require_run_index(run_index: int) -> None:
    if isinstance(run_index, bool) or not isinstance(run_index, int) or run_index < 1:
        raise ValueError(
            f"The run index is {run_index!r}, not an integer of at least 1. Run indices are "
            "1-based, exactly as TimingsSidecar.PathFor counts them: a run 0 would silently "
            "alias run 1 under any convention that maps both onto a real path.")


def _require_token(value: str, what: str) -> None:
    # Refused rather than escaped, exactly as TimingsSidecar.Write refuses: the same token has to
    # appear in the whitespace-separated run file this sidecar is paired against, so a token only
    # JSON escaping could carry could never be matched to a ranking anyway.
    if not value or any(
            character.isspace() or character in '"\\' or not character.isprintable()
            for character in value):
        raise ValueError(
            f"The {what} {value!r} is empty, or carries whitespace, a control character, or a "
            "character JSON would have to escape; the run file this sidecar pairs against could "
            "never hold the same token.")


def _require_span(value: float, what: str) -> None:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) \
            or value < 0:
        raise ValueError(
            f"The {what} is {value!r}, not a non-negative finite number. No clock runs backwards, "
            "so a negative span is a subtraction the wrong way round; and a NaN does not compare, "
            "so it would silently become whichever percentile a sort left it at.")


def _require_count(value: int, minimum: int, what: str) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise ValueError(
            f"The {what} is {value!r}, not an integer of at least {minimum}. A run that indexed "
            "nothing would post the fastest indexing time in the table.")

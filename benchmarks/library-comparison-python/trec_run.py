"""A TREC run-file writer that produces files ``TrecRunFile.Read`` accepts.

The run file is the entire boundary: this harness emits rankings and **nothing else**, and the one
``IrMetrics`` behind the repository's published BEIR figures scores what it reads back. So this
writer enforces the same invariants ``TrecRunFile.Write`` does, for the same reasons:

- queries in ordinal id order, ranks contiguous from 1, ``\\n`` line ends -- the same rankings
  must produce a byte-compatible file no matter which machine wrote it;
- **scores non-increasing within a query**, because trec_eval ignores the rank column and
  re-sorts by score, so a file whose score order contradicted its rank order would be scored
  differently by an outside checker than by ``IrMetrics``;
- no whitespace in any token (the format is whitespace-separated), no duplicate document within a
  query, no rank gaps;
- an empty ranking becomes the reserved ``RAGNET-EMPTY-RUN`` marker line, keeping "retrieved
  nothing" distinct from "query never processed" across the file format.

Scores are formatted by Python's shortest-round-trip ``repr``, which is invariant-culture by
construction (``.`` decimal separator, ``e`` exponents) -- both forms ``TrecRunFile.ParseScore``
accepts.
"""

from __future__ import annotations

from pathlib import Path

EMPTY_RUN_DOCUMENT_ID = "RAGNET-EMPTY-RUN"


def write(path: str | Path, runs: dict[str, list[tuple[str, float]]], run_tag: str) -> None:
    """Writes rankings-by-query-id (best first, ``(document_id, score)``) as a TREC run file."""
    _require_token(run_tag, "run tag")
    if not runs:
        raise ValueError(
            "The run set is empty. An entrant that processed zero queries lost its query set; a "
            "run file written from it would read as total retrieval failure.")

    lines: list[str] = []
    for query_id in sorted(runs):  # ordinal, as TrecRunFile.Write sorts
        _require_token(query_id, "query id")
        documents = runs[query_id]
        if not documents:
            lines.append(f"{query_id} Q0 {EMPTY_RUN_DOCUMENT_ID} 1 0 {run_tag}\n")
            continue

        seen: set[str] = set()
        previous_score = float("inf")
        for rank, (document_id, score) in enumerate(documents, start=1):
            _require_token(document_id, f"document id at '{query_id}' rank {rank}")
            if document_id == EMPTY_RUN_DOCUMENT_ID:
                raise ValueError(
                    f"Query '{query_id}' rank {rank} uses the reserved empty-run marker id.")
            if score != score or score in (float("inf"), float("-inf")):
                raise ValueError(f"Query '{query_id}' rank {rank} has non-finite score {score!r}.")
            if score > previous_score:
                raise ValueError(
                    f"Query '{query_id}' rank {rank} scores {score!r}, above the rank before it; "
                    "trec_eval re-sorts by score, so this file would mean two different rankings "
                    "depending on who reads it.")
            if document_id in seen:
                raise ValueError(
                    f"Query '{query_id}' ranks document '{document_id}' twice; IrMetrics would "
                    "award its gain twice, and a duplicate means the entrant bypassed "
                    "document-level pooling.")
            seen.add(document_id)
            previous_score = score
            lines.append(f"{query_id} Q0 {document_id} {rank} {score!r} {run_tag}\n")

    Path(path).write_bytes("".join(lines).encode("utf-8"))


def _require_token(value: str, what: str) -> None:
    if not value or any(character.isspace() for character in value):
        raise ValueError(
            f"The {what} {value!r} is empty or contains whitespace, which the "
            "whitespace-separated TREC format cannot carry.")

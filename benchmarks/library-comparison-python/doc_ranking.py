"""Chunk hits to a document ranking, exactly as ``DocumentRanking.TopDocuments`` does it.

The pooling is the **harness's protocol, not any entrant's own behaviour** -- the same rule the
.NET side applies: every entrant's hits are aggregated by the same steps in the same order (map to
parent document, **max-pool**, dedupe, *then* cut to top-k), so the comparison table compares
chunking-and-retrieval strategies rather than aggregation code. Cutting before pooling lets one
heavily chunked document occupy several of the k slots and evict documents that belong in the
ranking -- the ordering defect ``DocumentRanking``'s tests pin.

The self-exclusion (BEIR's ``if corpus_id != query_id``, MTEB's ``ignore_identical_ids``) happens
**before pooling too**, on the writer's side of the run-file boundary: the published run file must
already hold the post-exclusion ranking, or an outsider's trec_eval would score a different
ranking than IrMetrics does.

Ties on the pooled score are broken by ordinal document id -- arbitrary but repeatable, and the
same order ``string.CompareOrdinal`` gives the .NET rows (Python compares strings by code point).
"""

from __future__ import annotations


def top_documents(
    chunk_hits: list[tuple[str, float]],
    k: int,
    excluded_document_id: str | None = None,
) -> list[tuple[str, float]]:
    """Max-pools ``(document_id, score)`` chunk hits and returns the best ``k``, best first."""
    if k <= 0:
        raise ValueError(f"k must be positive, got {k}")

    pooled: dict[str, float] = {}
    for document_id, score in chunk_hits:
        if document_id == excluded_document_id:
            continue
        best = pooled.get(document_id)
        if best is None or score > best:
            pooled[document_id] = score

    ranked = sorted(pooled.items(), key=lambda entry: (-entry[1], entry[0]))
    return ranked[:k]

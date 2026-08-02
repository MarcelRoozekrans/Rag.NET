"""Haystack at its defaults, emitting rankings and nothing else.

Defaults measured (read from the installed source at haystack-ai 3.0.0, recorded in
``docs/reference/library-comparison-defaults.md`` before this entrant was written):

- **Chunker:** ``DocumentSplitter()`` -- ``split_by = "word"``, ``split_length = 200``,
  ``split_overlap = 0``, ``split_threshold = 0``
  (``haystack/components/preprocessors/document_splitter.py``). "word" splits on single spaces.
- **Store/retrieval:** ``InMemoryDocumentStore()`` with
  ``embedding_similarity_function = "dot_product"`` -- the default, and the one place a library's
  default differs from cosine (``haystack/document_stores/in_memory/document_store.py``). The
  pinned embedder L2-normalises, so dot product and cosine coincide numerically here; the default
  is still recorded and used as found.
- **Top-k:** ``InMemoryEmbeddingRetriever``'s own default is ``top_k = 10``
  (``haystack/components/retrievers/in_memory/embedding_retriever.py``) -- the only library in
  the table whose default equals the metric's cutoff. The run still passes the protocol depth
  explicitly, because chunk-to-document pooling needs the overshoot every entrant gets.
- **Embedder:** haystack-ai 3.0.0's core ships OpenAI, Azure and mock embedders only --
  ``OpenAIDocumentEmbedder(model="text-embedding-ada-002")`` is the closest thing to a default
  (``haystack/components/embedders/openai_document_embedder.py``); the sentence-transformers
  embedders of the 2.x era are no longer in core. The pinned ``all-MiniLM-L6-v2`` fills
  ``Document.embedding`` exactly where an embedder component would, and the query vector goes to
  ``InMemoryEmbeddingRetriever.run(query_embedding=...)`` -- Haystack's own retrieval path does
  the ranking.
"""

from __future__ import annotations

from collections import Counter

from haystack import Document
from haystack.components.preprocessors import DocumentSplitter
from haystack.components.retrievers.in_memory import InMemoryEmbeddingRetriever
from haystack.document_stores.in_memory import InMemoryDocumentStore

NAME = "haystack"
RUN_TAG = "haystack-ai-3.0.0"
DESCRIPTION = (
    "DocumentSplitter defaults (200 words, 0 overlap), InMemoryDocumentStore dot_product "
    "(its default; vectors are unit-length), pinned all-MiniLM-L6-v2 filling Document.embedding")


def build(documents, embed_many):
    """Indexes the corpus through Haystack's own paths; returns ``(retrieve, max_units)``."""
    splitter = DocumentSplitter()
    splitter.warm_up()
    chunks = splitter.run(
        documents=[Document(content=d.retrieval_text, meta={"doc_id": d.id}) for d in documents]
    )["documents"]

    units_per_document = Counter(chunk.meta["doc_id"] for chunk in chunks)
    if len(units_per_document) != len(documents):
        raise RuntimeError(
            f"The splitter produced chunks for {len(units_per_document)} documents out of "
            f"{len(documents)}; a document that contributed nothing is unretrievable.")

    for chunk, vector in zip(chunks, embed_many([chunk.content for chunk in chunks])):
        chunk.embedding = vector.tolist()

    store = InMemoryDocumentStore()
    written = store.write_documents(chunks)
    if written != len(chunks):
        raise RuntimeError(
            f"The store accepted {written} of {len(chunks)} chunks. Haystack ids are content "
            "hashes, so a silent drop here means two chunks collided and a document lost part "
            "of its text -- a different corpus, not a smaller one.")

    retriever = InMemoryEmbeddingRetriever(document_store=store)

    def retrieve_by_vector(query_vector: list[float], k: int) -> list[tuple[str, float]]:
        hits = retriever.run(query_embedding=query_vector, top_k=k)["documents"]
        return [(hit.meta["doc_id"], float(hit.score)) for hit in hits]

    def retrieve(query_text: str, k: int) -> list[tuple[str, float]]:
        return retrieve_by_vector(embed_many([query_text])[0].tolist(), k)

    return retrieve, max(units_per_document.values()), len(chunks)

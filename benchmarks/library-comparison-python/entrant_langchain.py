"""LangChain at its defaults, emitting rankings and nothing else.

Defaults measured (read from the installed source at the pinned versions, recorded in
``docs/reference/library-comparison-defaults.md`` before this entrant was written):

- **Chunker:** ``RecursiveCharacterTextSplitter()`` with every default -- ``chunk_size = 4000``
  **characters**, ``chunk_overlap = 200``, ``length_function = len``
  (``langchain_text_splitters/base.py``, ``TextSplitter.__init__``, langchain-text-splitters
  1.1.2), separators ``["\\n\\n", "\\n", " ", ""]``, ``keep_separator = True``
  (``langchain_text_splitters/character.py``, ``RecursiveCharacterTextSplitter``).
- **Store/retrieval:** ``InMemoryVectorStore`` -- langchain-core's own in-process store, cosine
  similarity (``langchain_core/vectorstores/in_memory.py``: "computes cosine similarity for
  search using numpy"). LangChain has no default store; this is the core package's only
  in-process one, the analogue of the choice every entrant gets.
- **Top-k:** the library default is ``k = 4`` (``similarity_search*``); the run retrieves at the
  protocol depth instead, because the published metric is nDCG@10 and its cutoff is a property of
  the measurement, not of any library (defaults page, protocol rule).
- **Embedder:** LangChain has no default embedder (``Embeddings`` is abstract in core); its
  natural companion package's default is ``OpenAIEmbeddings(model="text-embedding-ada-002")``
  (langchain-openai 1.4.1). The pinned ``all-MiniLM-L6-v2`` is injected through the public
  ``Embeddings`` interface, so LangChain's own upsert and search paths do the embedding calls.
"""

from __future__ import annotations

from collections import Counter

from langchain_core.documents import Document
from langchain_core.embeddings import Embeddings
from langchain_core.vectorstores import InMemoryVectorStore
from langchain_text_splitters import RecursiveCharacterTextSplitter

NAME = "langchain"
RUN_TAG = "langchain-core-1.5.3"
DESCRIPTION = (
    "RecursiveCharacterTextSplitter defaults (4000 chars, 200 overlap), InMemoryVectorStore "
    "cosine, pinned all-MiniLM-L6-v2 via the Embeddings interface")


class _PinnedEmbeddings(Embeddings):
    """The pinned embedder behind LangChain's own ``Embeddings`` seam."""

    def __init__(self, embed_many) -> None:
        self._embed_many = embed_many

    def embed_documents(self, texts: list[str]) -> list[list[float]]:
        return [vector.tolist() for vector in self._embed_many(texts)]

    def embed_query(self, text: str) -> list[float]:
        return self.embed_documents([text])[0]


def build(documents, embed_many):
    """Indexes the corpus through LangChain's own paths; returns ``(retrieve, max_units)``."""
    splitter = RecursiveCharacterTextSplitter()
    chunks = splitter.split_documents(
        Document(page_content=d.retrieval_text, metadata={"doc_id": d.id}) for d in documents)

    units_per_document = Counter(chunk.metadata["doc_id"] for chunk in chunks)
    if len(units_per_document) != len(documents):
        raise RuntimeError(
            f"The splitter produced chunks for {len(units_per_document)} documents out of "
            f"{len(documents)}; a document that contributed nothing is unretrievable.")

    store = InMemoryVectorStore(embedding=_PinnedEmbeddings(embed_many))
    store.add_documents(chunks)

    def retrieve(query_text: str, k: int) -> list[tuple[str, float]]:
        return [
            (hit.metadata["doc_id"], float(score))
            for hit, score in store.similarity_search_with_score(query_text, k=k)
        ]

    return retrieve, max(units_per_document.values()), len(chunks)

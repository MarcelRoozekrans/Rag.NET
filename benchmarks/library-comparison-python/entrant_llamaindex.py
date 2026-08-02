"""LlamaIndex at its defaults, emitting rankings and nothing else.

Defaults measured (read from the installed source at llama-index-core 0.14.23, recorded in
``docs/reference/library-comparison-defaults.md`` before this entrant was written):

- **Chunker:** ``Settings.node_parser`` defaults to ``SentenceSplitter()``
  (``llama_index/core/settings.py``) -- ``chunk_size = DEFAULT_CHUNK_SIZE = 1024`` **tokens**,
  ``chunk_overlap = SENTENCE_CHUNK_OVERLAP = 200`` (``constants.py``,
  ``node_parser/text/sentence.py``), counted with the default tokenizer: tiktoken's encoding for
  ``gpt-3.5-turbo`` (cl100k_base), from the cache bundled with the package
  (``llama_index/core/utils.py``, ``get_tokenizer``).
- **Store/retrieval:** ``VectorStoreIndex.from_documents`` defaults to ``SimpleVectorStore``
  (``storage/storage_context.py``), scored with ``SimilarityMode.DEFAULT = "cosine"``
  (``base/embeddings/base.py``; ``indices/query/embedding_utils.py``).
- **Top-k:** the library default is ``similarity_top_k = DEFAULT_SIMILARITY_TOP_K = 2``
  (``constants.py``); the run retrieves at the protocol depth instead -- nDCG@10's cutoff is a
  property of the measurement (defaults page, protocol rule). 2 is the sharpest default in the
  table and is recorded as a library fact.
- **Embedder:** the default resolves to ``OpenAIEmbedding()`` -- OpenAI ``text-embedding-ada-002``
  (``embeddings/utils.py`` ``resolve_embed_model("default")``; llama-index-embeddings-openai
  0.6.0) -- and raises without an API key. The pinned ``all-MiniLM-L6-v2`` is injected through
  the public ``BaseEmbedding`` seam via ``Settings.embed_model``, so LlamaIndex's own ingestion
  and query paths do the embedding calls. What LlamaIndex embeds per node is its own default
  composition (``get_content(metadata_mode=EMBED)``); with no metadata attached that is the chunk
  text.
"""

from __future__ import annotations

from collections import Counter

from llama_index.core import Document, Settings, VectorStoreIndex
from llama_index.core.base.embeddings.base import BaseEmbedding

NAME = "llamaindex"
RUN_TAG = "llama-index-core-0.14.23"
DESCRIPTION = (
    "SentenceSplitter defaults (1024 cl100k tokens, 200 overlap), SimpleVectorStore cosine, "
    "pinned all-MiniLM-L6-v2 via Settings.embed_model")


class _PinnedEmbedding(BaseEmbedding):
    """The pinned embedder behind LlamaIndex's own ``BaseEmbedding`` seam."""

    def __init__(self, embed_many) -> None:
        super().__init__(model_name="all-MiniLM-L6-v2")
        # BaseEmbedding is a pydantic model; keep the callable out of its field machinery.
        object.__setattr__(self, "_embed_many", embed_many)

    def _get_query_embedding(self, query: str) -> list[float]:
        return self._embed_many([query])[0].tolist()

    def _get_text_embedding(self, text: str) -> list[float]:
        return self._embed_many([text])[0].tolist()

    def _get_text_embeddings(self, texts: list[str]) -> list[list[float]]:
        return [vector.tolist() for vector in self._embed_many(texts)]

    async def _aget_query_embedding(self, query: str) -> list[float]:
        return self._get_query_embedding(query)

    async def _aget_text_embedding(self, text: str) -> list[float]:
        return self._get_text_embedding(text)


def build(documents, embed_many):
    """Indexes the corpus through LlamaIndex's own paths; returns ``(retrieve, max_units)``."""
    Settings.embed_model = _PinnedEmbedding(embed_many)

    index = VectorStoreIndex.from_documents(
        [Document(text=d.retrieval_text, id_=d.id) for d in documents])

    units_per_document = Counter(
        node.ref_doc_id for node in index.docstore.docs.values())
    if len(units_per_document) != len(documents):
        raise RuntimeError(
            f"The splitter produced nodes for {len(units_per_document)} documents out of "
            f"{len(documents)}; a document that contributed nothing is unretrievable.")

    def retrieve(query_text: str, k: int) -> list[tuple[str, float]]:
        retriever = index.as_retriever(similarity_top_k=k)
        return [
            (hit.node.ref_doc_id, float(hit.score))
            for hit in retriever.retrieve(query_text)
        ]

    return retrieve, max(units_per_document.values()), len(index.docstore.docs)

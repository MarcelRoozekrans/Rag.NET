"""A content-addressed vector cache in ``EmbeddingCache``'s exact on-disk format.

Used read-write against this harness's own directory (``<cache>/embeddings-python``, identity
``PYTHON_MODEL_IDENTITY``) so a re-run pays only for texts it has not seen. The format is
``EmbeddingCache``'s deliberately -- same key bytes, same entry layout -- so anyone auditing the
two caches can compare entries with one decoder.

The identity is hashed into every key -- ``SHA-256(len(identity) LE || identity || NUL || text)``,
``EmbeddingCache.ComputeKey``'s exact bytes -- and the Python harness **never writes under the
.NET identity or into the .NET ``embeddings`` directory**. Two pipelines proven equivalent are
still two pipelines; letting one satisfy the other's cache lookups is the cross-model reuse the
key design exists to prevent, so this harness is kept out of that cache twice over (different
directory AND different identity salt).

Entry layout (``EmbeddingCache``): 8-byte magic ``RAGNETE1``, the 32-byte key digest, an int32
dimension, then the float32 vector, all little-endian. Anything that does not agree -- magic,
digest, dimension, length -- is a miss, never a partial answer.

**Timed spans never read this cache from disk.** One ``.vec`` read costs ~10 ms with a cold OS
page cache and ~0.7 ms with a hot one, and one read per text inside an indexing span made
identical runs differ by up to 23x on run order alone -- a figure about the page cache, not the
library. So the harness ``prefetch``\\ es every vector a run will need into memory before any
timing starts, and the timed passes call ``serve``, which answers from that memory only and
refuses anything the prefetch never saw. A text with no entry at prefetch time is a **cold
cache** and raises rather than silently embedding (which would put the confound back one level
up); embedding during prefetch happens only on an explicit ``--warm-cache`` run, and the
hit/miss counters always describe what the disk really held.
"""

from __future__ import annotations

import hashlib
import struct
from pathlib import Path

import numpy as np

_MAGIC = b"RAGNETE1"
_DIGEST_LENGTH = 32
_HEADER_LENGTH = len(_MAGIC) + _DIGEST_LENGTH + 4


def compute_key(model_identity: str, text: str) -> str:
    identity_bytes = model_identity.encode("utf-8")
    text_bytes = text.encode("utf-8")
    payload = struct.pack("<i", len(identity_bytes)) + identity_bytes + b"\x00" + text_bytes
    return hashlib.sha256(payload).hexdigest()


class VectorCache:
    """One directory of ``.vec`` entries under one model identity."""

    def __init__(self, directory: str | Path, model_identity: str) -> None:
        self._directory = Path(directory)
        self._model_identity = model_identity
        self._prefetched: dict[str, np.ndarray] = {}
        self.hits = 0
        self.misses = 0

    def _path_for(self, key: str) -> Path:
        return self._directory / key[:2] / (key + ".vec")

    def try_read(self, text: str) -> np.ndarray | None:
        key = compute_key(self._model_identity, text)
        path = self._path_for(key)
        try:
            data = path.read_bytes()
        except OSError:
            return None
        return _try_parse(data, key)

    def write(self, text: str, vector: np.ndarray) -> None:
        key = compute_key(self._model_identity, text)
        path = self._path_for(key)
        path.parent.mkdir(parents=True, exist_ok=True)
        vector32 = np.ascontiguousarray(vector, dtype="<f4")
        payload = (
            _MAGIC + bytes.fromhex(key) + struct.pack("<i", vector32.shape[0]) + vector32.tobytes())
        path.write_bytes(payload)

    def get_or_embed(self, texts: list[str], embed) -> list[np.ndarray]:
        """One vector per text, embedding only the distinct texts not already stored."""
        results: list[np.ndarray | None] = [None] * len(texts)
        missing_texts: list[str] = []
        slots_by_text: dict[str, list[int]] = {}

        for index, text in enumerate(texts):
            cached = self.try_read(text)
            if cached is not None:
                results[index] = cached
                self.hits += 1
                continue
            self.misses += 1
            slots = slots_by_text.get(text)
            if slots is None:
                slots_by_text[text] = [index]
                missing_texts.append(text)
            else:
                slots.append(index)

        if missing_texts:
            embedded = embed(missing_texts)
            if len(embedded) != len(missing_texts):
                raise RuntimeError(
                    f"The embedder was handed {len(missing_texts)} texts and returned "
                    f"{len(embedded)} vectors; every vector after the first mismatch would be "
                    "stored against the wrong text.")
            for text, vector in zip(missing_texts, embedded):
                self.write(text, vector)
                for slot in slots_by_text[text]:
                    results[slot] = vector

        return results  # type: ignore[return-value]

    def prefetch(self, texts: list[str], embed=None) -> list[np.ndarray]:
        """Reads every text's vector from disk into memory, BEFORE any timing starts.

        This is the only method that touches disk during a measured run, and it runs outside
        every timed span. Texts with no entry mean the cache is **cold** for this corpus:
        without ``embed`` that raises, naming the count, because silently embedding here would
        hide that this run paid model and disk costs no other run paid. Passing ``embed`` (the
        explicit ``--warm-cache`` path) embeds and stores the misses instead -- loudly counted
        in ``misses``, so the sidecar still says what the disk really held.

        Returns one vector per text so it can stand in as the rehearsal pass's embed callback;
        hit/miss counters move here, once per occurrence, exactly as ``get_or_embed`` counted.
        """
        if embed is None:
            def embed(missing_texts):
                raise RuntimeError(
                    f"{len(missing_texts)} of the {len(texts)} texts in this prefetch batch "
                    f"have no entry under {self._directory} (identity "
                    f"{self._model_identity!r}): the vector cache is cold. Timed spans serve "
                    "vectors from memory and never embed -- embedding here would reintroduce "
                    "the very cost the prefetch excludes -- so warm the cache explicitly with "
                    "--warm-cache and re-run.")

        vectors = self.get_or_embed(texts, embed)
        for text, vector in zip(texts, vectors):
            self._prefetched[compute_key(self._model_identity, text)] = vector
        return vectors

    def serve(self, texts: list[str]) -> list[np.ndarray]:
        """One prefetched vector per text, from memory only -- what the timed spans call.

        Deliberately refuses any text ``prefetch`` never saw, even if its entry exists on
        disk: a disk read inside a timed span is exactly what prefetching exists to exclude.
        Counters do not move here; they described the disk once, at prefetch time.
        """
        keys = [compute_key(self._model_identity, text) for text in texts]
        missing = sum(1 for key in keys if key not in self._prefetched)
        if missing:
            raise RuntimeError(
                f"{missing} of the {len(texts)} texts in this batch were never prefetched. "
                "Every text a timed span embeds must be covered by the prefetch pass; serving "
                "it from disk or the model here would put I/O back inside the span.")
        return [self._prefetched[key] for key in keys]


def _try_parse(data: bytes, key: str) -> np.ndarray | None:
    if len(data) < _HEADER_LENGTH or data[: len(_MAGIC)] != _MAGIC:
        return None
    stored_digest = data[len(_MAGIC): len(_MAGIC) + _DIGEST_LENGTH].hex()
    if stored_digest != key:
        return None
    (dimension,) = struct.unpack_from("<i", data, len(_MAGIC) + _DIGEST_LENGTH)
    if dimension <= 0 or len(data) != _HEADER_LENGTH + dimension * 4:
        return None
    return np.frombuffer(data, dtype="<f4", offset=_HEADER_LENGTH)

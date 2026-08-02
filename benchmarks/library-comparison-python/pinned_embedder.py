"""The pinned embedder, replicated over onnxruntime so every Python row measures the same model.

The comparison matches exactly one element across entrants: ``all-MiniLM-L6-v2``, the exact ONNX
export the .NET harness runs (``RAGNET_ONNX_EMBED_MODEL``/``RAGNET_ONNX_EMBED_VOCAB``, pinned by
revision and SHA-256 in ``nightly.yml``). This module runs **that same file** through onnxruntime
rather than going through ``sentence-transformers``, deliberately: sentence-transformers would
download its own copy of the model at its own revision and run it through PyTorch with its own
tokenizer stack, which is a second model to verify for no gain. Here the model bytes are shared
and only the runtime differs -- and ``identity_check.py`` proves the vectors match
``OnnxEmbeddingGenerator``'s numerically before any entrant row is trusted.

The pipeline mirrors ``OnnxEmbeddingGenerator`` step for step:

- ``\\n``, ``\\r``, ``\\t`` are substituted with a single space **before** tokenization
  (``BertWordPieceTokenization.SubstituteWhitespace``: the .NET normalizer would delete them,
  merging the words on either side into one the document never contained).
- WordPiece over the same ``vocab.txt``, lowercased, **accents NOT stripped** -- measured, not
  assumed: HF's ``BertNormalizer`` with ``strip_accents=None`` strips accents when lowercasing
  (the reference-BERT behaviour), but ``Microsoft.ML.Tokenizers``' ``BertTokenizer`` at default
  ``BertOptions`` -- the pipeline behind every published figure -- does not: it leaves ``ü``
  intact and WordPiece maps the accented word to ``[UNK]``. With ``strip_accents=None`` the
  vector for ``"anti-Müllerian hormone. It’s café naïveté."`` diverged
  from the .NET dump by 0.166 max-abs; with ``strip_accents=False`` it matched all 384 floats
  bitwise, so ``False`` is pinned here. The corpus-wide comparison in ``identity_check.py``
  demonstrates the match over every SciFact and ArguAna text rather than asserting it.
- ``[CLS] content [SEP]`` with the content truncated to ``256 - 2`` tokens, so truncation can
  never drop the trailing ``[SEP]``.
- one padded batch per pass; **padding is excluded from the mean** -- masked rows contribute to
  neither the sum nor the divisor, which is what makes a text's vector independent of what it was
  batched with. [CLS] and [SEP] ARE included, matching sentence-transformers' mean pooling.
- L2 normalisation, norm accumulated in float64 exactly as ``L2NormalizeInPlace`` does.
"""

from __future__ import annotations

import numpy as np
import onnxruntime
from tokenizers import Tokenizer
from tokenizers.models import WordPiece
from tokenizers.normalizers import BertNormalizer
from tokenizers.pre_tokenizers import BertPreTokenizer

MAX_TOKENS = 256
_SPECIAL_TOKENS_PER_SEQUENCE = 2  # [CLS] and [SEP]

#: The .NET embedding cache's identity string, verbatim (``BeirHarness.ModelIdentity``) -- used
#: only to *read* .NET-produced vectors for the identity comparison.
DOTNET_MODEL_IDENTITY = (
    "all-MiniLM-L6-v2/onnx maxTokens=256 mean-pooled-excluding-padding l2-normalised"
)

#: What salts this harness's own cache keys. Deliberately NOT the .NET identity: the Python
#: pipeline is a replica proven equivalent, not the same code, and sharing an identity would let
#: a Python-written vector satisfy a .NET cache lookup -- the exact cross-model reuse the cache's
#: key design exists to prevent.
PYTHON_MODEL_IDENTITY = DOTNET_MODEL_IDENTITY + " python-onnxruntime"


class PinnedEmbedder:
    """``all-MiniLM-L6-v2`` over onnxruntime, in ``OnnxEmbeddingGenerator``'s exact pipeline."""

    def __init__(self, model_path: str, vocab_path: str, batch_size: int = 32) -> None:
        tokenizer = Tokenizer(WordPiece.from_file(vocab_path, unk_token="[UNK]"))
        # strip_accents=False, NOT the HF default of None (which strips when lowercasing):
        # measured against OnnxEmbeddingGenerator -- see the module docstring.
        tokenizer.normalizer = BertNormalizer(
            clean_text=True, handle_chinese_chars=True, strip_accents=False, lowercase=True)
        tokenizer.pre_tokenizer = BertPreTokenizer()
        self._tokenizer = tokenizer

        vocabulary = tokenizer.get_vocab()
        self._cls_id = vocabulary["[CLS]"]
        self._sep_id = vocabulary["[SEP]"]
        self._pad_id = vocabulary["[PAD]"]

        self._session = onnxruntime.InferenceSession(
            model_path, providers=["CPUExecutionProvider"])
        input_names = {i.name for i in self._session.get_inputs()}
        self._send_attention_mask = "attention_mask" in input_names
        self._send_token_type_ids = "token_type_ids" in input_names
        output_names = [o.name for o in self._session.get_outputs()]
        self._output_name = (
            "last_hidden_state" if "last_hidden_state" in output_names else output_names[0])
        # Without an attention mask the model attends to the padding, so padding must not exist:
        # one text per pass -- the same rule OnnxEmbeddingGenerator applies.
        self._batch_size = batch_size if self._send_attention_mask else 1

    def encode_ids(self, text: str) -> list[int]:
        """``[CLS] content [SEP]``, content truncated to the per-sequence budget."""
        substituted = text.replace("\n", " ").replace("\r", " ").replace("\t", " ")
        ids = self._tokenizer.encode(substituted, add_special_tokens=False).ids
        content = ids[: MAX_TOKENS - _SPECIAL_TOKENS_PER_SEQUENCE]
        return [self._cls_id, *content, self._sep_id]

    def embed(self, texts: list[str]) -> list[np.ndarray]:
        """One float32 unit-length vector per text, batch-independent by construction."""
        sequences = [self.encode_ids(text) for text in texts]
        vectors: list[np.ndarray] = []
        for start in range(0, len(sequences), self._batch_size):
            vectors.extend(self._embed_batch(sequences[start:start + self._batch_size]))
        return vectors

    def _embed_batch(self, sequences: list[list[int]]) -> list[np.ndarray]:
        batch_size = len(sequences)
        sequence_length = max(len(s) for s in sequences)

        input_ids = np.full((batch_size, sequence_length), self._pad_id, dtype=np.int64)
        attention_mask = np.zeros((batch_size, sequence_length), dtype=np.int64)
        for row, sequence in enumerate(sequences):
            input_ids[row, : len(sequence)] = sequence
            attention_mask[row, : len(sequence)] = 1

        feeds: dict[str, np.ndarray] = {"input_ids": input_ids}
        if self._send_attention_mask:
            feeds["attention_mask"] = attention_mask
        if self._send_token_type_ids:
            feeds["token_type_ids"] = np.zeros_like(input_ids)

        (hidden,) = self._session.run([self._output_name], feeds)
        if hidden.ndim != 3 or hidden.shape[0] != batch_size or hidden.shape[1] != sequence_length:
            raise RuntimeError(
                f"Model output has shape {hidden.shape}; token-level hidden states "
                f"[{batch_size}, {sequence_length}, dimension] are required. A rank-2 shape is a "
                "pooled export, whose pooling cannot be shown to exclude padding.")

        vectors = []
        for row in range(batch_size):
            token_count = int(attention_mask[row].sum())
            # Masked rows contribute to neither the sum nor the divisor; float32 arithmetic like
            # OnnxEmbeddingGenerator.MeanPool, then a float64-accumulated norm like
            # L2NormalizeInPlace.
            pooled = hidden[row, :token_count, :].sum(axis=0, dtype=np.float32) / np.float32(token_count)
            norm = np.sqrt(np.sum(pooled.astype(np.float64) ** 2))
            if norm > 0:
                pooled = pooled / np.float32(norm)
            vectors.append(pooled)
        return vectors

"""Proves the Python-side embedder is the .NET ``OnnxEmbeddingGenerator``, numerically.

The comparison's design matches exactly one element across entrants -- the pinned
``all-MiniLM-L6-v2`` -- and rejects tables that silently measure embedders. The .NET entrants
prove identity by construction (they hand the ``OnnxEmbeddingGenerator`` instance to the
library); the Python entrants cannot, so this script proves it the way Stage 1's Task 4 did: a
small battery of **known strings**, each embedded by ``OnnxEmbeddingGenerator`` directly on the
.NET side and by ``PinnedEmbedder`` here, compared float for float. The battery is chosen to
cover every step where the two pipelines could diverge -- plain prose, punctuation, accented
characters (the one divergence actually found: see ``pinned_embedder``'s docstring), CJK,
embedded ``\\n\\r\\t`` whitespace (the substitution rule), and a text long enough to truncate at
the 256-token budget. If these match, the tokenizer, the truncation, the pooling and the
normalisation all match; a corpus-scale sweep could only re-demonstrate the same equality at a
thousand times the cost.

Usage::

    uv run python identity_check.py --write-battery <dir>   # emit <name>.input.txt files
    # the .NET side (from the repository root): embeds each .input.txt with the exact
    # generator behind every published figure and writes <name>.txt beside it --
    #   RAGNET_IDENTITY_BATTERY_DIR=<dir> dotnet test \
    #     tests/Rag.NET.Benchmarks.Quality.IntegrationTests \
    #     --filter "DisplayName~DumpsEachBatteryInputsVector"
    uv run python identity_check.py <dir>                   # compare, one line per entry

Both sides read the text from the same ``<name>.input.txt`` bytes, so no shell quoting can make
them embed different strings.

Environment: ``RAGNET_ONNX_EMBED_MODEL``, ``RAGNET_ONNX_EMBED_VOCAB``.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

import numpy as np

from pinned_embedder import PinnedEmbedder

#: name -> text. Each targets a specific place the two pipelines could disagree.
BATTERY: dict[str, str] = {
    # the tokenizer's ordinary path
    "plain": "The Python harness must hand this exact sentence to the pinned "
             "all-MiniLM-L6-v2 embedder.",
    # punctuation splitting in basic tokenization
    "punctuation": "Wait -- really?! \"Quoted,\" (parenthesised); semi:colons, "
                   "ellipsis... and/or slashes -- end.",
    # accents and typographic quotes: the divergence actually found and fixed --
    # Microsoft.ML.Tokenizers does not strip accents; HF's strip_accents=None default does
    "accented": "anti-Müllerian hormone. It’s café naïveté.",
    # CJK: both sides must individually tokenize han characters
    "cjk": "BERT 处理中文 characters と日本語 too.",
    # \n \r \t: the SubstituteWhitespace rule -- without it the .NET normalizer deletes these
    # and merges the words either side
    "whitespace": "alpha\n\nbeta\tgamma\rdelta  double-space  end",
    # long enough that the 254-content-token truncation decides the vector
    "truncating": ("Sentence number one about retrieval quality benchmarks and chunking "
                   "strategies for dense embedding models. ") * 20,
}


def write_battery(directory: Path) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for name, text in BATTERY.items():
        (directory / f"{name}.input.txt").write_bytes(text.encode("utf-8"))
    print(f"Wrote {len(BATTERY)} battery inputs to {directory}")


def check(directory: Path) -> int:
    embedder = PinnedEmbedder(
        os.environ["RAGNET_ONNX_EMBED_MODEL"], os.environ["RAGNET_ONNX_EMBED_VOCAB"])

    failed = False
    for name, text in BATTERY.items():
        dump = directory / f"{name}.txt"
        dotnet = np.array(
            [float(line) for line in dump.read_text(encoding="utf-8-sig").split()],
            dtype=np.float32)
        python = embedder.embed([text])[0]

        if dotnet.shape != python.shape:
            print(f"{name}: DIMENSION MISMATCH {dotnet.shape} vs {python.shape}")
            failed = True
            continue

        bitwise = int((dotnet == python).sum())
        max_diff = float(np.max(np.abs(dotnet.astype(np.float64) - python.astype(np.float64))))
        cosine = float(np.dot(dotnet.astype(np.float64), python.astype(np.float64)))
        verdict = "OK" if max_diff < 1e-6 else "DIVERGED"
        print(f"{name}: {bitwise}/{len(dotnet)} floats bitwise-equal, "
              f"max |diff| = {max_diff:.3e}, cosine = {cosine:.15f}  [{verdict}]")
        failed |= verdict != "OK"

    return 1 if failed else 0


def main() -> int:
    if len(sys.argv) == 3 and sys.argv[1] == "--write-battery":
        write_battery(Path(sys.argv[2]))
        return 0
    if len(sys.argv) == 2:
        return check(Path(sys.argv[1]))
    print(__doc__)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())

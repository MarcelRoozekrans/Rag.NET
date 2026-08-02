"""BEIR's on-disk layout, read the way ``BeirLoader`` reads it.

This mirrors ``src/Rag.NET.Benchmarks.Quality/BeirLoader.cs`` deliberately, because the Python
entrants must ingest byte-for-byte the same texts as every .NET entrant: ``retrieval_text`` is
``(title + " " + text).strip()`` -- BEIR's own SentenceBERT join, the one the published parity
figures were measured with -- and the judged-query filter is membership of ``qrels/test.tsv``,
which is the harness's protocol for *which queries a run retrieves for* (``BeirHarness
.JudgedQueries``). A loader that tolerated a malformed line would produce a short corpus that
scores like a retrieval defect, so every parse failure raises and names the file and line.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

TITLE_TEXT_SEPARATOR = " "
QRELS_HEADER = "query-id\tcorpus-id\tscore"


@dataclass(frozen=True)
class DatasetDescriptor:
    """The counts a loaded dataset must match -- ``BeirDatasetDescriptor``'s numbers, restated.

    Restated rather than shared because nothing machine-readable crosses the language boundary;
    the .NET scoring test re-asserts the same counts from its own descriptor, so a disagreement
    between the two copies fails loudly on the first run.
    """

    name: str
    document_count: int
    query_count: int
    judged_query_count: int
    excludes_self_retrieved_document: bool


DATASETS = {
    "scifact": DatasetDescriptor("scifact", 5183, 1109, 300, False),
    "arguana": DatasetDescriptor("arguana", 8674, 1406, 1406, True),
    "fiqa": DatasetDescriptor("fiqa", 57638, 6648, 648, True),
}


@dataclass(frozen=True)
class BeirDocument:
    id: str
    title: str
    text: str
    retrieval_text: str


@dataclass(frozen=True)
class BeirQuery:
    id: str
    text: str


@dataclass(frozen=True)
class BeirDataset:
    descriptor: DatasetDescriptor
    documents: list[BeirDocument]
    queries: list[BeirQuery]
    judged_query_ids: frozenset[str]

    def judged_queries(self) -> list[BeirQuery]:
        """The queries a run retrieves for: exactly those qrels judges, in queries.jsonl order."""
        return [q for q in self.queries if q.id in self.judged_query_ids]


def load_dataset(cache_directory: str | Path, name: str) -> BeirDataset:
    """Loads an extracted BEIR dataset from ``<cache>/<name>`` and checks it is whole."""
    descriptor = DATASETS.get(name)
    if descriptor is None:
        raise ValueError(f"Unknown dataset '{name}'. Known: {sorted(DATASETS)}")

    directory = Path(cache_directory) / name
    documents = _load_corpus(directory / "corpus.jsonl")
    queries = _load_queries(directory / "queries.jsonl")
    judged = _load_judged_query_ids(directory / "qrels" / "test.tsv")

    _require(len(documents) == descriptor.document_count, directory,
             f"corpus has {len(documents)} documents, expected {descriptor.document_count}")
    _require(len(queries) == descriptor.query_count, directory,
             f"queries has {len(queries)} entries, expected {descriptor.query_count}")
    _require(len(judged) == descriptor.judged_query_count, directory,
             f"qrels judges {len(judged)} queries, expected {descriptor.judged_query_count}")

    return BeirDataset(descriptor, documents, queries, judged)


def _load_corpus(path: Path) -> list[BeirDocument]:
    documents = []
    for line_number, record in _jsonl(path):
        title = record.get("title") or ""
        text = _required(record, "text", path, line_number)
        doc_id = _required(record, "_id", path, line_number)
        # BeirLoader.LoadCorpus: string.Concat(title, separator, text).Trim()
        documents.append(BeirDocument(
            doc_id, title, text, (title + TITLE_TEXT_SEPARATOR + text).strip()))
    if not documents:
        raise ValueError(f"'{path}' yielded no documents; a truncated download, not an empty corpus.")
    return documents


def _load_queries(path: Path) -> list[BeirQuery]:
    queries = []
    for line_number, record in _jsonl(path):
        queries.append(BeirQuery(
            _required(record, "_id", path, line_number),
            _required(record, "text", path, line_number)))
    if not queries:
        raise ValueError(f"'{path}' yielded no queries; a truncated download, not an empty file.")
    return queries


def _load_judged_query_ids(path: Path) -> frozenset[str]:
    """Distinct query ids in the test split -- judged membership only, never the judgements.

    The judgements themselves (which documents, how relevant) deliberately never enter this
    harness: retrieval must not see them, and scoring happens on the .NET side of the run-file
    boundary. Rows scoring 0 still mark their query as judged, exactly as ``BeirLoader`` keeps
    them.
    """
    judged: set[str] = set()
    with open(path, encoding="utf-8") as file:
        for line_number, line in enumerate(file, start=1):
            line = line.rstrip("\n").rstrip("\r")
            if line_number == 1:
                if line.strip().lower() != QRELS_HEADER.lower():
                    raise ValueError(
                        f"'{path}' line 1 is {line!r}, not the BEIR qrels header; the header is "
                        "required so a headerless file cannot silently lose its first judgement.")
                continue
            if not line.strip():
                continue
            columns = line.split("\t")
            if len(columns) != 3:
                raise ValueError(f"'{path}' line {line_number} has {len(columns)} columns, not 3.")
            int(columns[2].strip())  # a non-integer score is a malformed file, not a 0
            judged.add(columns[0].strip())
    if not judged:
        raise ValueError(f"'{path}' contains a header but no judgements.")
    return frozenset(judged)


def _jsonl(path: Path):
    with open(path, encoding="utf-8") as file:
        for line_number, line in enumerate(file, start=1):
            if not line.strip():
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"'{path}' line {line_number} is not valid JSON: {error}") from error
            if not isinstance(record, dict):
                raise ValueError(f"'{path}' line {line_number} is not a JSON object.")
            yield line_number, record


def _required(record: dict, name: str, path: Path, line_number: int) -> str:
    value = record.get(name)
    if not isinstance(value, str):
        raise ValueError(f"'{path}' line {line_number} has no string '{name}' property.")
    return value


def _require(condition: bool, directory: Path, message: str) -> None:
    if not condition:
        raise ValueError(f"'{directory}': {message}. A partial dataset scores like a retrieval defect.")

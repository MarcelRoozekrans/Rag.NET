# Cross-language format fixtures

`python-written-format-fixture.trec.timings.json` is a **format fixture with invented numbers,
written by `benchmarks/library-comparison-python/timings.py`** — not a measurement artefact from
any real run. `PythonTimingsFixtureTests` reads it with `TimingsSidecar.Read` to prove the Python
writer and the .NET reader agree about the format. Regenerate (from
`benchmarks/library-comparison-python/`) with:

    uv run --no-project python -c "import timings; timings.write(r'<repo>\tests\Rag.NET.Benchmarks.Quality.Tests\Fixtures\python-written-format-fixture.trec', run_tag='format-fixture-not-a-measurement', indexing_seconds=4.25, query_latencies_milliseconds={'q-2': 12.25, 'q-10': 3.5, 'q-1': 0.125}, embedding_cache_hits=7, embedding_cache_misses=3, unit_count=5, max_units_per_document=2)"

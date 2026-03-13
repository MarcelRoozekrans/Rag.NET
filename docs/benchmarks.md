# Rag.NET Benchmarks

Measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) v0.15.8 on .NET 10.0.4, Windows 11 (25H2).
Hardware: Intel Core i9-12900HK 2.50 GHz, 20 logical cores.

Run yourself:

```bash
dotnet run --project benchmarks/Rag.NET.Benchmarks -c Release -- --filter "*"
```

---

## Chunking

`MaxChunkSize = 512, Overlap = 50`. Input sizes approximate character counts.

| Strategy | Input | Mean | Allocated |
|----------|-------|-----:|----------:|
| Fixed | 500 chars | 377 ns | 1.70 KB |
| Fixed | 5 KB | 3.1 us | 16.74 KB |
| Fixed | 50 KB | 29.1 us | 158.37 KB |
| Recursive | 500 chars | 916 ns | 2.94 KB |
| Recursive | 5 KB | 8.1 us | 31.91 KB |
| Recursive | 50 KB | 93.6 us | 315.54 KB |
| TokenAware | 500 chars | 18.0 us | 6.10 KB |
| TokenAware | 5 KB | 156 us | 36.74 KB |
| TokenAware | 50 KB | 1,750 us | 388.89 KB |

**Notes:**
- `TokenAware` uses `TiktokenTokenizer` (cl100k_base) — encoding/decoding overhead is 20–60× that of character-based strategies.
- The overhead is paid once per chunk window, not per document; most real ingestion time is dominated by embedding API latency.
- Use `TokenAware` when chunk size precision matters (code, URLs, dense text); `Recursive` is the safe default for prose.

---

## Parsers

| Parser | Input | Mean | Allocated |
|--------|-------|-----:|----------:|
| Text | 1 KB | 1.7 us | 9.81 KB |
| Text | 100 KB | 172.5 us | 403.64 KB |
| Markdown | 1 KB | 1.9 us | 11.95 KB |
| Markdown | 100 KB | 331.4 us | 599.17 KB |
| HTML | 5 sections | 108.4 us | 81.71 KB |
| HTML | 100 sections | 1,087 us | 615.26 KB |
| CSV | 500 rows | 333.4 us | 468.55 KB |
| JSON | 100 elements | 74.3 us | 49.42 KB |

---

## Pipeline (end-to-end ingestion)

50 KB document with mocked embedder and no-op vector store to isolate parse + chunk overhead.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| IngestAsync (50 KB) | 377.9 us | 629.14 KB |

The pipeline benchmark uses `RecursiveChunkingStrategy`. Embedding and vector-store calls are mocked — add your provider's p99 latency for real-world estimates.

# Rag.NET.Demo.Rijksoverheid

A proof of concept on a real public site. It ingests the *Familie, zorg en gezondheid* theme of
[rijksoverheid.nl](https://www.rijksoverheid.nl) from the site's sitemap, then answers questions in
Dutch and lists the pages each answer came from.

## Why this site

Rijksoverheid.nl publishes its texts under [CC0 1.0](https://www.rijksoverheid.nl/copyright), so
they can be ingested and the demo shown publicly without asking permission. Photos and videos are
not covered by that waiver; the demo ingests text only.

Many health sites, thuisarts.nl among them, allow personal use only and require permission for
anything else. Check a site's terms before pointing this sample at it; `robots.txt` allowing a
crawl does not grant a licence.

## What it shows

- Two providers combined, because neither alone has every page. The site's FAQ answers are
  missing from its sitemap, so `WebCrawlerDataProvider` goes first, bounded to the theme with
  `IncludedUrlPrefixes`. `SitemapDataProvider` then adds pages no link led to, taking one theme of
  a site with ~120,000 URLs in 1,209 sitemap shards.
- `RespectRobotsTxt` and `MinimumRequestInterval` on both, so the walk is polite to a server you
  do not run: one request every 500 ms.
- FAQ index pages left out of the index: they list question titles with no answers, so they match
  questions well and answer none of them.
- `AddHtmlParser()` dropping navigation, header and footer, so chunks hold page content.
- Streaming answers with the source URLs listed (each page's document id is its URL).

## Run it

Needs Docker (Postgres with pgvector via Testcontainers) and an OpenAI or OpenRouter API key.

```bash
export OPENAI_API_KEY=sk-...
dotnet run --project samples/Rag.NET.Demo.Rijksoverheid
```

Or through [OpenRouter](https://openrouter.ai), which serves the same chat and embedding models:

```bash
export OPENROUTER_API_KEY=sk-or-...
dotnet run --project samples/Rag.NET.Demo.Rijksoverheid
```

With only `OPENROUTER_API_KEY` set, the sample talks to `https://openrouter.ai/api/v1` and uses
the OpenRouter ids `openai/gpt-4o-mini` and `openai/text-embedding-3-small`. `OPENAI_API_KEY`
wins when both are set.

Optional settings:

| Variable | Default | Meaning |
|---|---|---|
| `RAG_MAX_PAGES` | `150` | Pages to ingest before stopping the walk |
| `OPENAI_CHAT_MODEL` | `gpt-4o-mini` | Chat model |
| `OPENAI_EMBEDDING_MODEL` | `text-embedding-3-small` | Embedding model; multilingual, 1536 dimensions |
| `EMBEDDING_DIMENSIONS` | `1536` | Vector size; set it when you change the embedding model, e.g. `1024` for `baai/bge-m3` |
| `OPENAI_BASE_URL` | OpenAI, or OpenRouter's URL | Any other OpenAI-compatible endpoint |

Ingesting 150 pages takes a few minutes. The theme's pages are spread across the site's
sitemap shards, so each shard read yields only a few of them. The database lives in a throwaway
container, so every run ingests again.

## Measuring retrieval

```bash
export OPENAI_API_KEY=sk-...   # or OPENROUTER_API_KEY
export COHERE_API_KEY=...     # optional: adds the reranker variants
dotnet run --project samples/Rag.NET.Demo.Rijksoverheid -- --eval
```

`--eval` ingests the whole theme, then runs the 40 questions in `eval/vragen.json` through each
retrieval variant: dense, hybrid (BM25 + vector), HyDE, hybrid + HyDE, MMR, and with
`COHERE_API_KEY` set, a multilingual reranker over dense and over hybrid. It prints Hit@1,
Hit@5, Recall@5 and MRR@10 per variant, scored per page, and how many questions each variant
ranks better or worse than dense. With 40 questions, a difference of a few hundredths is noise;
the better/worse counts show whether a gain is broad or rides on two questions. Below the table,
it lists each question a variant moved, with the rank of the first answer page before and after
and the page the variant ranked first instead.

MMR is in the list because the site publishes one FAQ answer under up to four theme paths:
four documents with the same text, which can fill the top 5 with a single answer.

Each question names the pages that answer it, FAQ answers included. The questions are written the way a citizen asks,
not copied from page titles. A question whose page was not ingested is dropped and counted.

### Results

Two runs on 2026-11-09 through OpenRouter, with `openai/text-embedding-3-small` embeddings and
`openai/gpt-4o-mini` writing the HyDE passages. They ingested 1,021 and 1,014 pages with none
failed, and all 40 questions had an answer page in the index. No Cohere key, so no reranker
variants. Where the runs differ, the table shows both values.

| variant | Hit@1 | Hit@5 | Recall@5 | MRR@10 | MRR better / worse vs dense |
|---|---:|---:|---:|---:|---:|
| dense | 0.825 | 0.975 | 0.872 | 0.884–0.886 | — |
| hybrid | 0.800 | 0.950 | 0.795 | 0.868 | 2 / 6, 2 / 6 |
| hyde | 0.850–0.875 | 0.975–1.000 | 0.887–0.892 | 0.905–0.913 | 5 / 3, 5 / 2 |
| hybrid+hyde | 0.850–0.875 | 0.975 | 0.848–0.861 | 0.893–0.912 | 5 / 4, 6 / 4 |
| mmr | 0.800–0.825 | 0.975–1.000 | 0.786–0.806 | 0.881–0.902 | 4 / 2, 4 / 1 |

Dense finds an answer page in its top 5 for 39 of the 40 questions and ranks one first for 33.
That leaves the other variants little to improve. Hybrid ranks more questions worse than better
in every run so far. HyDE moves a few more questions up than down, but five against two or three
on 40 questions can still be chance, and it costs a chat call before every answer. MMR's lower
Recall@5 is by design: it keeps the copies of one FAQ answer out of the top 5. The chat mode
therefore uses dense retrieval with the top 5 chunks.

**Why the answer lists grew.** The first runs scored dense at Hit@5 0.850 and MRR@10 0.680.
Listing the questions each variant moved showed variants losing points for pages that do answer
the question but were missing from `eval/vragen.json`. `--pool <file>` writes every page any
variant ranks in its top 5. In one run, 295 such pages were not in the answer lists. Each was
judged against its question from the full page text, and the 44 that answer it were added. A page
on the same topic that does not answer the question does not count. One wrongly rejected page
turned up in a spot check, so the lists may still miss a few answers.

Example questions:

- *Wanneer moet ik naar de huisarts en wanneer naar de ggz?*
- *Wat doet de overheid tegen de tijgermug?*
- *Wat is eerstelijnszorg?*

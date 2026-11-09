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
the better/worse counts show whether a gain is broad or rides on two questions.

MMR is in the list because the site publishes one FAQ answer under up to four theme paths:
four documents with the same text, which can fill the top 5 with a single answer.

Each question names the pages that answer it, FAQ answers included. The questions are written the way a citizen asks,
not copied from page titles. A question whose page was not ingested is dropped and counted.

### Results

One run on 2026-11-08 through OpenRouter, with `openai/text-embedding-3-small` embeddings and
`openai/gpt-4o-mini` writing the HyDE passages. It ingested 1,018 pages with none failed, and
all 40 questions had their answer page in the index. No Cohere key, so no reranker variants.

| variant | Hit@1 | Hit@5 | Recall@5 | MRR@10 | MRR better / worse vs dense |
|---|---:|---:|---:|---:|---:|
| dense | 0.575 | 0.850 | 0.799 | 0.680 | — |
| hybrid | 0.550 | 0.775 | 0.732 | 0.657 | 3 / 7 |
| hyde | 0.575 | 0.875 | 0.837 | 0.705 | 6 / 7 |
| hybrid+hyde | 0.625 | 0.875 | 0.807 | 0.720 | 7 / 5 |
| mmr | 0.600 | 0.850 | 0.784 | 0.698 | 6 / 5 |

Hybrid is the one clear result: it ranks seven questions worse than dense and three better.
HyDE, MMR and hybrid + HyDE each move about as many questions up as down, so their higher
averages come from a handful of questions and may not hold on another run. HyDE also costs a
chat call before every answer. The chat mode therefore uses dense retrieval with the top 5
chunks.

Six questions had no answer page in dense's top 5, among them *Hoeveel wiet mag ik thuis
hebben?* (answered by the page on softdrugs and coffeeshops) and *Wat is een pgb?*. `--eval`
prints the full list.

Example questions:

- *Wanneer moet ik naar de huisarts en wanneer naar de ggz?*
- *Wat doet de overheid tegen de tijgermug?*
- *Wat is eerstelijnszorg?*

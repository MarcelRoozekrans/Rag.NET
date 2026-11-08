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

- `SitemapDataProvider` taking one section of a site with ~120,000 URLs in 1,209 sitemap shards,
  using `IncludedUrlPrefixes`.
- `RespectRobotsTxt` and `MinimumRequestInterval`, so the walk is polite to a server you do not
  run: one request every 500 ms.
- `AddHtmlParser()` dropping navigation, header and footer, so chunks hold page content.
- Streaming answers with the source URLs listed (each page's document id is its URL).

## Run it

Needs Docker (Postgres with pgvector via Testcontainers) and an OpenAI API key.

```bash
export OPENAI_API_KEY=sk-...
dotnet run --project samples/Rag.NET.Demo.Rijksoverheid
```

Optional settings:

| Variable | Default | Meaning |
|---|---|---|
| `RAG_MAX_PAGES` | `150` | Pages to ingest before stopping the walk |
| `OPENAI_CHAT_MODEL` | `gpt-4o-mini` | Chat model |
| `OPENAI_EMBEDDING_MODEL` | `text-embedding-3-small` | Embedding model; multilingual, 1536 dimensions |

Ingesting 150 pages takes a few minutes. The theme's pages are spread across the site's
sitemap shards, so each shard read yields only a few of them. The database lives in a throwaway
container, so every run ingests again.

Example questions:

- *Wanneer moet ik naar de huisarts en wanneer naar de ggz?*
- *Wat doet de overheid tegen de tijgermug?*
- *Wat is eerstelijnszorg?*

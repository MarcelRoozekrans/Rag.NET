---
id: positioning
title: Positioning
sidebar_position: 4
---

# Where Rag.NET Sits

**Rag.NET is a retrieval pipeline for .NET.** Not an agent framework, not an LLM gateway, not a
vector database. This page says who should pick it, who should not, and what the evidence for each
claim is — every factual assertion below links to a page that measured it or read it, because a
positioning page that argues from adjectives is a brochure.

Two pages carry the evidence:

- [Library Comparison at Defaults](./reference/library-comparison.md) — **measured**: retrieval
  quality and cost against four other libraries, from a committed harness an outsider can re-run.
- [Library Comparison — Scope](./reference/library-comparison-scope.md) — **read**: what each
  library ships, cited to source at pinned versions.

## The short version

Rag.NET is worth choosing when **the RAG pipeline itself is the product surface** and the host is
.NET: many document formats, many source systems, retrieval quality you intend to tune, and an
operational posture — telemetry, resilience, caching, security, evaluation — that has to survive
review. It is the wrong choice when you want an agent that occasionally searches, when your team
is Python-first, or when your requirement is a single integration this library does not have and a
larger ecosystem does.

## What the measurements actually support

**Retrieval quality is not a differentiator, and the honest reading is that it cannot be.** On
SciFact and ArguAna at every library's defaults, four of five entrants — Rag.NET, Semantic Kernel,
LangChain and LlamaIndex — sit within about 0.001–0.0014 nDCG@10 of each other, and Rag.NET is not
top of either column. Those rows are not separable on that measurement. Anyone claiming a RAG
library "retrieves better" needs to explain a gap larger than the one a single protocol change
produces on the same corpora (+0.031, −0.029), and no such gap exists here. **Do not choose
Rag.NET for its nDCG.**

**Query latency is a real .NET advantage, with a caveat that guts the headline version of it.**
Rag.NET answers in **0.3–0.4 ms** on SciFact, **0.9–1.1 ms** on ArguAna and **7.9–10.0 ms** on
FiQA — fastest of the five entrants on all three, 2–3× ahead of Semantic Kernel and two orders of
magnitude ahead of the Python entrants' 54–118 ms.

But that compares each library's *default in-memory store*, and the Python defaults are reference
implementations nobody deploys — Python-level scan loops. Point all five at a real Qdrant or
pgvector and this gap is the store's, not the library's. The honest reading of the Python
comparison is a fact about default stores, not about libraries.

**The .NET half of it is worth more, and it was not free.** An earlier version of this page said
Semantic Kernel was slightly faster than Rag.NET, and at the time it was. The dense scan was
allocating the whole corpus on every query and recomputing two constant norms per candidate;
fixing both made the control 4–5× faster and reversed the ordering. It is a genuine result against
a comparable .NET implementation on the same protocol — and it is also a reminder that a
measurement like this dates quickly, which is why every figure on
[the comparison page](./reference/library-comparison.md#cost-retrieval-latency-and-index-construction)
carries its date, its spread and its protocol.

## Against the .NET field

This is where the case is strongest, and it is a **scope** case rather than a quality one.

**Semantic Kernel is not really a competitor — it is a different layer.** SK is an agent and
orchestration framework. It has **no ingestion pipeline**: nothing chunks a document unless the
caller does, and its one splitting utility is `[Experimental]` with no default size. It has no
reranker in the retrieval path, no query transformation, no evaluation, and every vector-store
connector checked — InMemory, Azure AI Search, Qdrant, Pinecone, Weaviate, Postgres — **has never
shipped a stable version**, while `Microsoft.SemanticKernel` itself is stable at 1.79.0.

The corollary matters more than the criticism: **SK and Rag.NET compose rather than compete.** Both
sit on `Microsoft.Extensions.AI` abstractions. An SK agent calling a Rag.NET retriever is a
sensible architecture, and it is the one to reach for when the answer to "who decides to search"
is "the model".

**Kernel Memory was the closest analogue and is over.** Its packages are marked legacy on NuGet,
`0.98.250508.3` (2025-05-09) is the final release, and its own README calls it "an archived
research project". The comparison deliberately publishes **no number** against it — scoring a
project its authors archived invites the fair objection that the table picked something that could
not answer back.

That leaves a real gap in .NET, and filling it is Rag.NET's actual claim: 18 source connectors,
9 parser packages, 7 vector stores, RAPTOR, GraphRAG with Leiden community detection, HyDE and
multi-query, four answer engines, and REST/gRPC/MCP/CLI serving surfaces — none of which has a
maintained .NET equivalent today.

## Against the Python field

**Feature parity is close, and on breadth Rag.NET loses.** LlamaIndex core alone ships an
ingestion pipeline, file readers, four rerankers, HyDE, graph indexing, response synthesizers,
memory, evaluation, agents, tools and workflows. Haystack ships converters, rankers, a query
expander, agents, and IR metrics — nDCG, MRR, MAP, recall — in core. The scope table's marks favour
Rag.NET partly because it counts first-party packages, and Rag.NET has 70 of them while LangChain
core is deliberately abstraction-only with its concrete integrations one package away.

**On ecosystem, it is not close at all.** LangChain and LlamaIndex have hundreds of integration
packages. For any "can I connect to X" question where X is not among Rag.NET's 18 connectors and 6
stores, the Python libraries win, and no table in this repository shows that because counting it
consistently is a project rather than a page.

So the .NET-native argument has to carry the weight: a typed, DI-first, `IAsyncEnumerable`
streaming pipeline that deploys as part of an ASP.NET application, with OpenTelemetry and Polly
already wired, versus a Python service on the other side of a network hop. That is a real argument
in a .NET shop and not much of one anywhere else.

## Where Rag.NET loses outright

Stated here rather than left for a reader to discover:

- **No agent orchestration and no tool-calling surface.** Semantic Kernel, LlamaIndex and Haystack
  all ship one; Rag.NET ships neither. If the requirement is "an agent that decides when to
  retrieve", this is the wrong library and SK is the .NET answer.
- **Ecosystem breadth**, as above.
- **RAPTOR, GraphRAG and the answer engines are unmeasured.** They are scope facts. What any of
  them does to retrieval quality on any corpus is not measured by this repository — for Rag.NET or
  for anyone else. A capability is not a result.
- **Maturity is not evidenced.** Nothing in this repository counts adoption, contributors or
  issue latency, and 70 first-party packages is a packaging philosophy, not a proof of depth.

## Choosing

| If you… | Choose |
|---|---|
| Are on .NET and the RAG pipeline is the product | **Rag.NET** |
| Need an agent that decides when to retrieve | **Semantic Kernel** (optionally over Rag.NET retrieval) |
| Need an integration Rag.NET lacks, and can host Python | **LlamaIndex** or **LangChain** |
| Want evaluation-first RAG with IR metrics in the box | **Haystack** or **Rag.NET** |
| Are starting from Kernel Memory | Anything maintained — KM is archived |

## What this page is not

It is an argument, not a measurement, and it is written by the library's own repository. Every
number it leans on is on a page that states its own protocol, its own spreads and its own
limitations, and both of those pages publish results that do not flatter Rag.NET — it is not top of
either quality column, and it is the ✖ in the scope table's last two rows. **If this page and the
evidence pages ever disagree, the evidence pages are right.**

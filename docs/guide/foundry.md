---
id: foundry
title: Microsoft Foundry
sidebar_position: 15
---

# Microsoft Foundry

Rag.NET ships no Foundry package and needs none. The pipeline consumes two `Microsoft.Extensions.AI`
abstractions — `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>` — and resolves them
from the container when the pipeline is built, so any client that produces those two is wired the
same way. This page is the Foundry-shaped version of [Getting Started](../getting-started.md)'s
step 2; every step after it is unchanged.

Three shapes, differing only in how the client is constructed:

| Shape | Endpoint | Client |
|-------|----------|--------|
| Foundry in the cloud | `https://<resource>.openai.azure.com/openai/v1/` | `OpenAIClient`, package `OpenAI` |
| Foundry Local | the URL the local web service reports, plus `/v1` | the same `OpenAIClient`, different base URI |
| Existing Azure OpenAI code | the same resource | `AzureOpenAIClient`, package `Azure.AI.OpenAI` |

The third is listed because it still works, not because it is preferred: if your application already
builds an `AzureOpenAIClient`, `client.GetChatClient(deployment).AsIChatClient()` and
`client.GetEmbeddingClient(deployment).AsIEmbeddingGenerator()` register exactly as below and nothing
on this page changes. Microsoft's current guidance for new code is the `OpenAI` package against the
`/openai/v1/` route, which is what the examples use.

## Foundry in the cloud

```bash
dotnet add package Rag.NET
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Extensions.AI
dotnet add package Microsoft.Extensions.AI.OpenAI   # supplies AsIChatClient / AsIEmbeddingGenerator
dotnet add package OpenAI
dotnet add package Azure.Identity                   # keyless authentication only
```

### With an API key

```csharp
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

var foundry = new OpenAIClient(
    new ApiKeyCredential(Environment.GetEnvironmentVariable("FOUNDRY_KEY")!),
    new OpenAIClientOptions
    {
        Endpoint = new Uri("https://<resource>.openai.azure.com/openai/v1/"),
    });

var services = new ServiceCollection();

services.AddChatClient(foundry.GetChatClient("gpt-5-mini").AsIChatClient());
services.AddEmbeddingGenerator(
    foundry.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator());
```

The strings are **deployment names**, not model names. They happen to match when a deployment is
named after its model, and they stop matching the moment someone names one `chat-prod` — see
[what to pin](#what-to-pin) below, because one of those two names ends up in your index.

### Keyless, with Microsoft Entra ID

An API key grants full access to the resource and has to be rotated by hand. For anything long-lived
prefer a token credential; only the construction changes.

```csharp
using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

// The authentication-policy constructor is marked for evaluation only; without the suppression
// this is a compile error, not a warning.
#pragma warning disable OPENAI001

var foundry = new OpenAIClient(
    authenticationPolicy: new BearerTokenPolicy(
        new DefaultAzureCredential(), "https://ai.azure.com/.default"),
    options: new OpenAIClientOptions
    {
        Endpoint = new Uri("https://<resource>.openai.azure.com/openai/v1/"),
    });

#pragma warning restore OPENAI001

var services = new ServiceCollection();

services.AddChatClient(foundry.GetChatClient("gpt-5-mini").AsIChatClient());
services.AddEmbeddingGenerator(
    foundry.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator());
```

The token scope is `https://ai.azure.com/.default`, and the identity needs the Entra ID role
assignments for inference on the resource.

### Then the pipeline, unchanged

```csharp
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.PgVector;

services.AddRagNet(rag => rag
    .UsePgVector("Host=localhost;Database=ragdb;Username=postgres;Password=secret",
                 vectorDimensions: 1536));

var pipeline = services.BuildServiceProvider().GetRequiredService<IRagPipeline>();
```

### Models that are not OpenAI's

Foundry serves DeepSeek, Llama, Grok and the rest of the catalogue through that same endpoint and
the same credentials: deploy the model, put the deployment name where `gpt-5-mini` is, change
nothing else. Rag.NET's chat path goes through `GetChatClient(...).AsIChatClient()`, which calls
chat completions — the API every Foundry deployment supports, rather than the Responses API, which
some deployments reject with `400 Model not supported`.

What does change is the token accounting; see the third item in [what to pin](#what-to-pin).

## Foundry Local

Foundry Local runs models on the machine and exposes an OpenAI-compatible REST server, so the
wiring is the cloud wiring with a different base URI and a credential nobody checks.

Start the service first. It is hosted in-process by the `Microsoft.AI.Foundry.Local` SDK, which
downloads and loads a model and then starts the web service on a URL it reports back — take the URL
from the SDK rather than hard-coding a port, which is not stable. Microsoft's
[inference SDK walkthrough](https://learn.microsoft.com/azure/foundry-local/how-to/how-to-integrate-with-inference-sdks)
carries the full bootstrap.

```csharp
using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

// foundryLocalUrl is what the Foundry Local SDK reported, e.g. "http://127.0.0.1:52495"
var local = new OpenAIClient(
    new ApiKeyCredential("not-needed"),
    new OpenAIClientOptions { Endpoint = new Uri($"{foundryLocalUrl}/v1") });

var services = new ServiceCollection();

services.AddChatClient(local.GetChatClient(modelId).AsIChatClient());
```

### It answers; it does not embed

That snippet registers a chat client and stops there, and the omission is the point. The Foundry
Local REST surface is chat completions, audio transcription, token counting and model management —
[its reference](https://learn.microsoft.com/azure/foundry-local/reference/reference-rest) lists no
embeddings route. A RAG pipeline cannot run on a chat client alone: ingestion embeds every chunk and
retrieval embeds every query.

So pair it with an embedder. For a pipeline that stays entirely on the machine, that is
`Rag.NET.Embeddings.Onnx` — a local ONNX model, in-process, no external API:

```csharp
using Rag.NET.DependencyInjection;
using Rag.NET.Embeddings.Onnx;

services.AddRagNet(rag => rag
    .UseOnnxEmbeddings(o =>
    {
        o.ModelPath = "models/all-MiniLM-L6-v2.onnx";
        o.TokenizerVocabPath = "models/vocab.txt";
    }));
```

`UseOnnxEmbeddings` registers the `IEmbeddingGenerator<string, Embedding<float>>` itself, so there is
no `AddEmbeddingGenerator` call in this shape. The other option is the ordinary one — a cloud
embedder from the section above alongside the local chat client. Rag.NET never assumes the two come
from the same provider.

## What to pin

**The embedding identity Rag.NET stores is the deployment name.** After each successful store,
[embedding versioning](ingestion.md#embedding-versioning--re-indexing) stamps the document with the
identity read from the generator's `EmbeddingGeneratorMetadata`, and
`Microsoft.Extensions.AI.OpenAI` builds that metadata from the string you passed to
`GetEmbeddingClient` — so the stamp is `openai/<deployment-name>`, whatever model sits behind the
deployment. Repoint a deployment at a different model and every vector in the index is stale while
still stamped current: `ReindexStaleAsync` sees one unchanged name and finds nothing to do. Where a
deployment's model can change under you, pin the identity yourself:

```csharp
using Rag.NET.DependencyInjection;

// UseEmbeddingVersioning ships in Rag.NET.Storage.Sqlite
services.AddRagNet(rag => rag
    .UseEmbeddingVersioning(o => o.ModelId = "text-embedding-3-small@2024-05"));
```

**Vector dimensions are the deployment's, not the default's.** `vectorDimensions` on the store call
has to match what the embedding deployment returns — 1536 for `text-embedding-3-small`, 3072 for
`text-embedding-3-large` — and on a store that bakes the dimension into its schema it cannot be
changed afterwards. [Vector stores](vector-stores.md) has the per-store detail.

**Cost tracking estimates when the provider is quiet.** `UseCostBudgeting`
([Resilience](resilience.md)) records real token counts when the response reports both input and
output usage, and otherwise estimates both sides with the tiktoken cl100k tokenizer. Azure OpenAI
deployments report usage. For catalogue models that do not, the ledger still moves — on an estimate
produced by an OpenAI tokenizer, which is approximate for a model that does not tokenize like one.
Budgets enforced against those numbers are approximate in the same measure.

## Related

- [Getting Started](../getting-started.md) — the same six steps with OpenAI in this page's place
- [Choosing packages](choosing-packages.md) — what else a given pipeline needs
- [Resilience](resilience.md) — retries, fallback chains and cost budgets around the model boundary
- [Security](security.md) — what sits between your prompt and the model

# Rag.NET.Hosting

`Rag.NET.Hosting` binds a `RagNet` configuration section to a full pipeline: an OpenAI-compatible
chat client and embedding generator (covering OpenAI, Azure OpenAI, OpenRouter, Ollama, and LM
Studio, since they all speak the same wire API), plus one of three vector stores — `InMemory`,
`Qdrant`, or `PgVector`.

The extension method `AddRagNetPipelineFromConfiguration` validates the bound configuration
before registering anything: a missing chat or embeddings endpoint, model, or API key; an
absent or non-positive `RagNet:Embeddings:VectorDimensions`; or an unrecognised
`RagNet:VectorStore:Kind`. Every problem is reported by name — both the setting and the
configuration key that fixes it — in a single `InvalidOperationException` thrown while the host
is being built, rather than surfacing later as a confusing failure the first time the pipeline
is used.

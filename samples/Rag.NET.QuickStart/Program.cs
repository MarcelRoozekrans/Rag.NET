// Rag.NET.QuickStart — a straight-through walk of docs/getting-started.md's flow (ingest,
// re-ingest with Overwrite, retrieve, ask, ask streaming, delete), wired via Rag.NET.Hosting's
// AddRagNetPipelineFromConfiguration instead of registering IChatClient/IEmbeddingGenerator/
// IVectorStore by hand. See appsettings.json for what it needs to run and how to point it at a
// different provider — it defaults to a local Ollama instance and an in-memory vector store, so
// out of the box it needs no API key and no external service beyond Ollama itself.

using System.ClientModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rag.NET.Abstractions;
using Rag.NET.Hosting.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args });

try
{
    builder.Services.AddRagNetPipelineFromConfiguration(builder.Configuration);
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 1;
}

using var host = builder.Build();
var pipeline = host.Services.GetRequiredService<IRagPipeline>();

try
{
    await RunAsync(pipeline);
    return 0;
}
catch (Exception ex) when (ex is HttpRequestException or IOException or ClientResultException
    or InvalidOperationException)
{
    await Console.Error.WriteLineAsync(
        $"Could not reach the configured chat/embedding endpoint: {ex.Message}");
    await Console.Error.WriteLineAsync(
        "Is Ollama running locally (`ollama serve`, with `ollama pull llama3.2` and " +
        "`ollama pull nomic-embed-text`)? To use OpenAI instead, set RagNet__ChatClient__* and " +
        "RagNet__Embeddings__* environment variables — see appsettings.json.");
    return 1;
}

static async Task RunAsync(IRagPipeline pipeline)
{
    await IngestDocumentsAsync(pipeline);
    await ReingestWithOverwriteAsync(pipeline);
    await RetrieveAsync(pipeline);
    await AskAsync(pipeline);
    await AskStreamingAsync(pipeline);
    await DeleteAsync(pipeline);
}

static async Task IngestDocumentsAsync(IRagPipeline pipeline)
{
    var documentsPath = Path.Combine(AppContext.BaseDirectory, "documents");
    Console.WriteLine("Ingesting documents...");

    foreach (var file in Directory.GetFiles(documentsPath, "*.md"))
    {
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId(Path.GetFileNameWithoutExtension(file)),
            FileName = Path.GetFileName(file),
            ContentType = "text/markdown",
            Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["source"] = "quickstart" },
        };

        using var stream = File.OpenRead(file);
        var result = await pipeline.IngestAsync(stream, metadata);
        if (!result.IsSuccess)
            throw Fail($"Ingesting '{metadata.FileName}' failed", result.Error);

        Console.WriteLine($"  {metadata.FileName}: {result.Value.ChunksStored} chunks stored");
    }
}

static async Task ReingestWithOverwriteAsync(IRagPipeline pipeline)
{
    var file = Path.Combine(AppContext.BaseDirectory, "documents", "ragnet-overview.md");
    var metadata = new DocumentMetadata
    {
        DocumentId = new DocumentId("ragnet-overview"),
        FileName = "ragnet-overview.md",
        ContentType = "text/markdown",
    };

    using var stream = File.OpenRead(file);
    var result = await pipeline.IngestAsync(
        stream, metadata, options: new IngestionOptions { Overwrite = true });
    if (!result.IsSuccess)
        throw Fail("Re-ingesting 'ragnet-overview' failed", result.Error);

    Console.WriteLine(
        $"\nRe-ingested 'ragnet-overview' (Overwrite=true): {result.Value.ChunksStored} chunks stored");
}

static async Task RetrieveAsync(IRagPipeline pipeline)
{
    const string query = "What vector stores does Rag.NET support?";
    Console.WriteLine($"\nRetrieving without chat: \"{query}\"");

    var results = await pipeline.RetrieveAsync(query, new RetrievalOptions { TopK = 3 });
    if (!results.IsSuccess)
        throw Fail("Retrieval failed", results.Error);

    foreach (var result in results.Value)
        Console.WriteLine($"  [{result.Score:F2}] {Truncate(result.Chunk.Text, 80)}");
}

static async Task AskAsync(IRagPipeline pipeline)
{
    const string query = "What is the default chunking strategy?";
    Console.WriteLine($"\nAsking: \"{query}\"");

    var response = await pipeline.AskAsync(query);
    Console.WriteLine($"Answer: {response.Answer}");
    Console.WriteLine($"Sources used: {response.Sources.Count}");
}

static async Task AskStreamingAsync(IRagPipeline pipeline)
{
    const string query = "What does AddRagNetPipelineFromConfiguration validate?";
    Console.WriteLine($"\nAsking (streaming): \"{query}\"");

    await foreach (var update in pipeline.AskStreamingAsync(query))
    {
        if (update.Sources is { Count: > 0 })
            Console.WriteLine($"[Found {update.Sources.Count} source(s)]");

        if (update.TextDelta is not null)
            Console.Write(update.TextDelta);
    }

    Console.WriteLine();
}

static async Task DeleteAsync(IRagPipeline pipeline)
{
    Console.WriteLine("\nDeleting 'rag-net-hosting'...");
    await pipeline.DeleteAsync("rag-net-hosting");
    Console.WriteLine("Deleted.");
}

static string Truncate(string text, int length) =>
    text.Length <= length ? text : text[..length] + "...";

// Funnels a pipeline Result's failure through the same friendly catch RunAsync's caller uses,
// instead of printing a RagError.StorageFailed's full exception dump inline — that Result carries
// the raw Exception for callers who need it, but a getting-started demo does not.
static InvalidOperationException Fail(string context, RagError error) => error switch
{
    RagError.StorageFailed storageFailed => new InvalidOperationException(
        $"{context}: {storageFailed.Inner.Message}", storageFailed.Inner),
    _ => new InvalidOperationException($"{context}: {error}"),
};

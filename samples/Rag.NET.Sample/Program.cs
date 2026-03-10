using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.PgVector;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSQL")!;
var apiKey = builder.Configuration["OpenAI:ApiKey"]!;
var embeddingModel = builder.Configuration["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small";
var chatModel = builder.Configuration["OpenAI:ChatModel"] ?? "gpt-4o-mini";

builder.Services.AddEmbeddingGenerator(
    new OpenAI.Embeddings.EmbeddingClient(embeddingModel, apiKey).AsIEmbeddingGenerator());

builder.Services.AddChatClient(
    new OpenAI.Chat.ChatClient(chatModel, apiKey).AsIChatClient());

builder.Services.AddRagNet(rag => rag
    .UsePgVector(connectionString));

var app = builder.Build();

// Initialize pgvector schema
var vectorStore = app.Services.GetRequiredService<IVectorStore>() as PgVectorStore;
if (vectorStore is not null)
{
    await vectorStore.InitializeAsync().ConfigureAwait(false);
}

app.MapPost("/ingest", async (IRagPipeline pipeline, HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file")
        ?? throw new BadHttpRequestException("No file provided.");

    var metadata = new DocumentMetadata
    {
        DocumentId = Guid.NewGuid().ToString(),
        FileName = file.FileName,
        ContentType = file.ContentType,
    };

    using var stream = file.OpenReadStream();
    var result = await pipeline.IngestAsync(stream, metadata);

    return Results.Ok(result);
});

app.MapPost("/ask", async (IRagPipeline pipeline, AskRequest request) =>
{
    var response = await pipeline.AskAsync(request.Question);
    return Results.Ok(response);
});

app.MapPost("/search", async (IRagPipeline pipeline, SearchRequest request) =>
{
    var results = await pipeline.RetrieveAsync(request.Query);
    return Results.Ok(results);
});

app.Run();

internal sealed record AskRequest(string Question);

internal sealed record SearchRequest(string Query);

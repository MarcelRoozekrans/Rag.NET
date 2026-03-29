using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Parsers.Pdf;
using Rag.NET.PgVector;
using OpenAI;
using Testcontainers.PostgreSql;

// --- Start PostgreSQL container ---
Console.WriteLine("Starting PostgreSQL container...");
var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();
await postgres.StartAsync();
var connectionString = postgres.GetConnectionString();
Console.WriteLine("PostgreSQL ready.");

try
{
    // --- Configure services ---
    var provider = Environment.GetEnvironmentVariable("RAG_PROVIDER") ?? "ollama";
    var services = new ServiceCollection();

    if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("Set OPENAI_API_KEY environment variable.");

        var chatModel = Environment.GetEnvironmentVariable("OPENAI_CHAT_MODEL") ?? "gpt-4o-mini";
        var embeddingModel = Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
        var vectorDimensions = 1536;

        services.AddChatClient(
            new OpenAI.Chat.ChatClient(chatModel, apiKey).AsIChatClient());
        services.AddEmbeddingGenerator(
            new OpenAI.Embeddings.EmbeddingClient(embeddingModel, apiKey).AsIEmbeddingGenerator());

        services.AddRagNet(rag => rag
            .UsePgVector(connectionString, vectorDimensions)
            .AddPdfParser());

        Console.WriteLine($"Using OpenAI (chat: {chatModel}, embeddings: {embeddingModel})");
    }
    else
    {
        var ollamaEndpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434/v1";
        var chatModel = Environment.GetEnvironmentVariable("OLLAMA_CHAT_MODEL") ?? "llama3.2";
        var embeddingModel = Environment.GetEnvironmentVariable("OLLAMA_EMBEDDING_MODEL") ?? "all-minilm";
        var vectorDimensions = 384;

        var ollamaOptions = new OpenAIClientOptions { Endpoint = new Uri(ollamaEndpoint) };
        var ollamaCredential = new ApiKeyCredential("ollama");

        services.AddChatClient(
            new OpenAI.Chat.ChatClient(chatModel, ollamaCredential, ollamaOptions).AsIChatClient());
        services.AddEmbeddingGenerator(
            new OpenAI.Embeddings.EmbeddingClient(embeddingModel, ollamaCredential, ollamaOptions).AsIEmbeddingGenerator());

        services.AddRagNet(rag => rag
            .UsePgVector(connectionString, vectorDimensions)
            .AddPdfParser());

        Console.WriteLine($"Using Ollama at {ollamaEndpoint} (chat: {chatModel}, embeddings: {embeddingModel})");
    }

    // --- Optional: SaaS connector examples ---
    // Uncomment to ingest from Confluence or Slack instead of local files.
    //
    // services.AddConfluenceDataProvider(
    //     baseUrl:  "https://your-org.atlassian.net",
    //     email:    "user@your-org.com",
    //     apiToken: Environment.GetEnvironmentVariable("CONFLUENCE_API_TOKEN")!);
    //
    // services.AddSlackDataProvider(
    //     botToken: Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN")!,
    //     configure: opts => opts.ChannelId = "C01234ABCDE");

    var serviceProvider = services.BuildServiceProvider();

    // --- Initialize vector store ---
    var vectorStore = serviceProvider.GetRequiredService<IVectorStore>() as PgVectorStore;
    if (vectorStore is not null)
    {
        await vectorStore.InitializeAsync();
    }

    // --- Ingest documents ---
    var pipeline = serviceProvider.GetRequiredService<IRagPipeline>();
    var documentsPath = Path.Combine(AppContext.BaseDirectory, "documents");

    if (Directory.Exists(documentsPath))
    {
        var files = Directory.GetFiles(documentsPath);
        Console.WriteLine($"\nIngesting {files.Length} documents...");

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var contentType = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".md" => "text/markdown",
                ".txt" => "text/plain",
                ".pdf" => "application/pdf",
                ".csv" => "text/csv",
                ".json" => "application/json",
                ".html" => "text/html",
                _ => "text/plain",
            };

            var metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(fileName),
                FileName = fileName,
                ContentType = contentType,
            };

            using var stream = File.OpenRead(file);
            var result = await pipeline.IngestAsync(stream, metadata);
            Console.WriteLine($"  {fileName}: {(result.IsSuccess ? result.Value.ChunksStored : 0)} chunks stored");
        }
    }

    // --- Interactive Q&A loop ---
    Console.WriteLine("\nReady! Ask a question (or 'quit' to exit):\n");

    while (true)
    {
        Console.Write("> ");
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input) || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        await foreach (var update in pipeline.AskStreamingAsync(input))
        {
            if (update.Sources is { Count: > 0 })
            {
                Console.WriteLine($"\n[Found {update.Sources.Count} source(s)]");
            }

            if (update.TextDelta is not null)
            {
                Console.Write(update.TextDelta);
            }
        }

        Console.WriteLine("\n");
    }
}
finally
{
    Console.WriteLine("Stopping PostgreSQL container...");
    await postgres.DisposeAsync();
}

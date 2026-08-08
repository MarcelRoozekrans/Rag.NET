// Rag.NET MCP Tool — dotnet global tool host
//
// This host is configured, not edited. Its pipeline (chat client, embedding generator,
// vector store) is wired from an `appsettings.json` next to the working directory, or from
// environment variables — see the sample `appsettings.json` shipped with this package and
// this project's README. WebApplication.CreateBuilder(args) already layers appsettings.json,
// environment variables, and the command line, so no extra configuration source is added here.
//
// The bounded provider set this wiring supports: any OpenAI-compatible chat/embedding endpoint
// (OpenAI, Azure OpenAI, OpenRouter, Ollama, LM Studio), and InMemory, Qdrant, or PgVector for
// the vector store. A misconfigured or unsupported setting fails at startup with a message that
// names the setting and the configuration key that fixes it.
//
// Need a provider outside that set (Weaviate, Pinecone, Chroma, Azure AI Search, ONNX
// embeddings, a bespoke IChatClient)? Host Rag.NET.Mcp directly in your own application and
// register whatever you like — see https://github.com/rag-net/Rag.NET for examples.

using Rag.NET.Hosting.DependencyInjection;
using Rag.NET.Mcp.DependencyInjection;

var transport = ParseArg(args, "--transport") ?? "stdio";
var port = int.TryParse(ParseArg(args, "--port"), System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 5050;
var apiKey = ParseArg(args, "--api-key") ?? Environment.GetEnvironmentVariable("RAGNET_MCP_API_KEY");

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRagNetPipelineFromConfiguration(builder.Configuration);

var mcpBuilder = builder.Services.AddRagNetMcpServer();

if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
{
    // Configure ASP.NET Core to listen on the requested port.
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

    // Register HTTP (Streamable HTTP / SSE) transport.
    // app.MapMcp() below wires up the actual endpoints.
    mcpBuilder.WithHttpTransport(port);

    if (apiKey is not null)
    {
        mcpBuilder.WithApiKey(apiKey);
    }
}
else
{
    // Default: stdio transport — used when launched by an MCP client (e.g. Claude Desktop).
    mcpBuilder.WithStdioTransport();
}

var app = builder.Build();

if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
{
    // Map MCP Streamable HTTP endpoints (also exposes legacy SSE at /sse and /message).
    app.MapMcp("/mcp");

    Console.Error.WriteLine($"[ragnet-mcp] HTTP transport listening on http://0.0.0.0:{port}/mcp");
    if (apiKey is not null)
    {
        Console.Error.WriteLine("[ragnet-mcp] API key authentication enabled.");
    }

    await app.RunAsync().ConfigureAwait(false);
}
else
{
    // Stdio transport: RunAsync blocks and processes messages from stdin/stdout.
    await app.RunAsync().ConfigureAwait(false);
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static string? ParseArg(string[] args, string name)
{
    // Accepts both "--key=value" and "--key value" forms.
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))
        {
            return args[i][(name.Length + 1)..];
        }

        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }

    return null;
}

// Rag.NET MCP Tool — dotnet global tool host
//
// This program is a host scaffold. It does NOT register an IRagPipeline by itself
// because the actual pipeline depends on the user's choice of embedding generator,
// vector store, and chat client. To make this tool functional you must either:
//
//   1. Edit this file after install and add your pipeline registrations
//      (e.g. builder.Services.AddRagNetPipeline(...).WithQdrant(...).WithOpenAI(...))
//   2. Or use the Rag.NET.Mcp library directly in your own application.
//
// See https://github.com/rag-net/Rag.NET for configuration examples.

using Rag.NET.Mcp.DependencyInjection;

var transport = ParseArg(args, "--transport") ?? "stdio";
var port = int.TryParse(ParseArg(args, "--port"), System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 5050;
var apiKey = ParseArg(args, "--api-key") ?? Environment.GetEnvironmentVariable("RAGNET_MCP_API_KEY");

var builder = WebApplication.CreateBuilder(args);

// Register Rag.NET MCP tools (rag_retrieve, rag_ask, rag_ingest).
// NOTE: IRagPipeline must be registered in DI for these tools to work at runtime.
// Add your pipeline registrations here, for example:
//   builder.Services.AddRagNetPipeline()
//       .WithOpenAIEmbeddings(...)
//       .WithQdrant(...)
//       .WithOpenAIChatClient(...);
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

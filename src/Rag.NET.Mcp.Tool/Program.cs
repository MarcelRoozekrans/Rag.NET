// Rag.NET MCP Tool — dotnet global tool host
//
// This host is configured, not edited. Its pipeline (chat client, embedding generator,
// vector store) is wired from an `appsettings.json` next to the working directory, or from
// environment variables — see the sample `appsettings.json` shipped with this package and
// this project's README. Both host builders used below already layer appsettings.json,
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
using Rag.NET.Mcp.Tool;

var transport = ProgramArguments.Parse(args, "--transport") ?? "stdio";
var port = int.TryParse(ProgramArguments.Parse(args, "--port"), System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 5050;
var apiKey = ProgramArguments.Parse(args, "--api-key") ?? Environment.GetEnvironmentVariable("RAGNET_MCP_API_KEY");

// The two transports need different hosts, not just different MCP builder calls: HTTP needs
// Kestrel to accept connections, but stdio must NOT start a web server at all — an MCP stdio
// client owns this process's stdin/stdout, and a stray Kestrel listener on the default port was
// exactly the silent-failure this tool shipped with (see the package's README).
if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
{
    await RunHttpAsync(args, port, apiKey).ConfigureAwait(false);
}
else
{
    await RunStdioAsync(args).ConfigureAwait(false);
}

// ---------------------------------------------------------------------------
// Transports
// ---------------------------------------------------------------------------

static async Task RunHttpAsync(string[] args, int port, string? apiKey)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

    builder.Services.AddRagNetPipelineFromConfiguration(builder.Configuration);

    // Register the real HTTP (Streamable HTTP / SSE) transport. This extension lives in
    // ModelContextProtocol.AspNetCore, which Rag.NET.Mcp deliberately does not reference — so
    // it is called here, through the SDK builder Rag.NET.Mcp exposes via McpServerBuilder.Server,
    // rather than through a wrapper that package cannot honestly provide.
    builder.Services.AddRagNetMcpServer().Server.WithHttpTransport();

    var app = builder.Build();

    if (apiKey is not null)
    {
        app.Use(async (context, next) =>
        {
            var suppliedKey = context.Request.Headers.TryGetValue("X-Api-Key", out var value)
                ? value.ToString()
                : null;

            if (!ApiKeyAuthorization.IsAuthorized(suppliedKey, apiKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    // Map MCP Streamable HTTP endpoints (also exposes legacy SSE at /sse and /message).
    app.MapMcp("/mcp");

    Console.Error.WriteLine($"[ragnet-mcp] HTTP transport listening on http://0.0.0.0:{port}/mcp");
    if (apiKey is not null)
    {
        Console.Error.WriteLine("[ragnet-mcp] API key authentication enabled.");
    }

    await app.RunAsync().ConfigureAwait(false);
}

static async Task RunStdioAsync(string[] args)
{
    // A plain generic host — no Kestrel, no HTTP listener. Used when launched by an MCP client
    // (e.g. Claude Desktop), which is the default and the common case.
    var builder = Host.CreateApplicationBuilder(args);

    // Stdout is the MCP protocol channel for stdio transport; nothing else may write to it.
    // The default console logger writes to stdout, so every log line — including the host's own
    // "Now listening on..." banner from a web host — would corrupt the JSON-RPC stream a real
    // client is reading. This does not add a second console provider: AddConsole registers its
    // provider with TryAddEnumerable, so a second call only layers this option onto the one the
    // default host builder already added.
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddRagNetPipelineFromConfiguration(builder.Configuration);
    builder.Services.AddRagNetMcpServer().WithStdioTransport();

    var host = builder.Build();

    await host.RunAsync().ConfigureAwait(false);
}

// Rag.NET CLI ("ragnet") — a dotnet global tool for ingesting into, and querying, a Rag.NET
// pipeline from the shell.
//
// Like Rag.NET.Mcp.Tool, this host does no pipeline wiring of its own.
// AddRagNetPipelineFromConfiguration (Rag.NET.Hosting) binds the same `RagNet` configuration
// section — appsettings.json next to the working directory, or environment variables — and
// performs the same startup validation and the same InMemory warning. See this project's README
// for the configuration shape, and for why `evaluate` is not implemented.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Cli;
using Rag.NET.Hosting.DependencyInjection;

var parsed = CliArguments.Parse(args);

if (parsed.ShowHelp)
{
    CliOutput.WriteUsage(Console.Out);
    return 0;
}

if (parsed.Command is null || !CliOutput.IsKnownCommand(parsed.Command))
{
    await Console.Error.WriteLineAsync(
            parsed.Command is null ? "No command given." : $"Unknown command '{parsed.Command}'.")
        .ConfigureAwait(false);
    CliOutput.WriteUsage(Console.Error);
    return 1;
}

if (string.Equals(parsed.Command, "evaluate", StringComparison.OrdinalIgnoreCase))
{
    await Console.Error.WriteLineAsync(CliOutput.EvaluateDeferredMessage).ConfigureAwait(false);
    return 1;
}

if (string.IsNullOrWhiteSpace(parsed.Argument))
{
    await Console.Error.WriteLineAsync($"'{parsed.Command}' requires an argument.").ConfigureAwait(false);
    CliOutput.WriteUsage(Console.Error);
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await RunAsync(parsed, cts.Token).ConfigureAwait(false);

static async Task<int> RunAsync(CliArguments.ParsedArguments parsed, CancellationToken cancellationToken)
{
    // HostApplicationBuilderSettings with Args left unset deliberately excludes the command-line
    // configuration source: this CLI's own arguments (a bare file path, a multi-word question)
    // are not --key/--key=value pairs, and the default command-line configuration provider throws
    // on exactly that shape. appsettings.json and environment variables still layer in normally.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings());

    // Stdout is this tool's output channel — a command's JSON result, meant to be piped onward.
    // Route every log line to stderr instead: the same fix Rag.NET.Mcp.Tool's stdio transport
    // needed so its own output channel (MCP JSON-RPC there, a command's JSON result here) never
    // shares a stream with log output.
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    try
    {
        builder.Services.AddRagNetPipelineFromConfiguration(builder.Configuration);
    }
    catch (InvalidOperationException ex)
    {
        await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
        return 1;
    }

    using var host = builder.Build();
    var pipeline = host.Services.GetRequiredService<IRagPipeline>();

    try
    {
        return string.Equals(parsed.Command, "ingest", StringComparison.OrdinalIgnoreCase)
            ? await RunIngestAsync(pipeline, parsed, cancellationToken).ConfigureAwait(false)
            : await RunQueryAsync(pipeline, parsed, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
    {
        await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
        return 1;
    }
}

static async Task<int> RunIngestAsync(
    IRagPipeline pipeline, CliArguments.ParsedArguments parsed, CancellationToken cancellationToken)
{
    var result = await IngestCommand
        .RunAsync(pipeline, parsed.Argument!, parsed.Overwrite, cancellationToken)
        .ConfigureAwait(false);
    CliOutput.WriteJson(Console.Out, result);
    return result.Errors.Count > 0 ? 1 : 0;
}

static async Task<int> RunQueryAsync(
    IRagPipeline pipeline, CliArguments.ParsedArguments parsed, CancellationToken cancellationToken)
{
    var result = await QueryCommand
        .RunAsync(pipeline, parsed.Argument!, parsed.TopK, cancellationToken)
        .ConfigureAwait(false);
    CliOutput.WriteJson(Console.Out, result);
    return 0;
}

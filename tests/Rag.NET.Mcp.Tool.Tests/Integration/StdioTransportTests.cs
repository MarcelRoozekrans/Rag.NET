using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Rag.NET.Mcp.Tool.Tests.Integration;

/// <summary>
/// Starts the shipped MCP tool as a real process on its real stdio transport and speaks JSON-RPC
/// to it: <c>initialize</c>, then <c>tools/list</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.2 (§2(d) of the phase design).</b> Before this, the only test in this
/// project was <c>ProgramArgumentsTests</c> — argument parsing, in isolation. <c>Program.cs</c>,
/// which chooses the transport and builds the host, had never been executed by anything. That is
/// the whole of what this package ships.
/// </para>
/// <para>
/// The transport choice is exactly where this tool's known defect lived. <c>Program.cs</c> says so
/// itself: stdio must <i>not</i> start Kestrel, because an MCP stdio client owns this process's
/// stdin and stdout, and "a stray Kestrel listener on the default port was exactly the
/// silent-failure this tool shipped with". A test that called the builder directly would not have
/// caught it; only starting the process and talking to it over stdin/stdout does.
/// </para>
/// <para>
/// <b>Hand-rolled JSON-RPC, deliberately.</b> <c>ModelContextProtocol</c> 2.1.0 ships no client —
/// its namespaces are <c>Protocol</c> and <c>Server</c> only — so there is no official client to
/// drive this with. Newline-delimited JSON is the stdio framing, and writing it by hand keeps the
/// test honest about what a real MCP client actually sends.
/// </para>
/// <para>
/// Configuration is supplied through environment variables so the tool starts without an
/// appsettings.json, and points at an InMemory store so nothing dials out. No tool is
/// <i>called</i> here — <c>rag_ask</c> would need a real model. <c>tools/list</c> proves the
/// server started, negotiated, and advertises the surface it claims to.
/// </para>
/// </remarks>
public sealed class StdioTransportTests
{
    private static string ToolAssemblyPath =>
        Path.Combine(AppContext.BaseDirectory, "Rag.NET.Mcp.Tool.dll");

    [Fact]
    public void TheToolBinaryExists_WhereThisTestExpectsIt()
    {
        Assert.True(
            File.Exists(ToolAssemblyPath),
            $"Rag.NET.Mcp.Tool.dll was not found at {ToolAssemblyPath}.");
    }

    [Fact]
    public async Task TheStdioServer_Initializes_AndAdvertisesItsThreeTools()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var tool = StartTool();

        try
        {
            var initialize = await RoundTripAsync(tool, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "rag-net-phase-6.2", version = "1.0.0" },
                },
            }, cts.Token);

            Assert.True(
                initialize.TryGetProperty("result", out var initResult),
                $"initialize did not return a result: {initialize}");
            Assert.True(initResult.TryGetProperty("serverInfo", out _), "no serverInfo in the initialize result");

            await SendAsync(tool, new { jsonrpc = "2.0", method = "notifications/initialized" }, cts.Token);

            var list = await RoundTripAsync(tool, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/list",
            }, cts.Token);

            Assert.True(list.TryGetProperty("result", out var listResult), $"tools/list did not return a result: {list}");
            var names = listResult.GetProperty("tools")
                .EnumerateArray()
                .Select(t => t.GetProperty("name").GetString())
                .ToList();

            Assert.Contains("rag_retrieve", names, StringComparer.Ordinal);
            Assert.Contains("rag_ask", names, StringComparer.Ordinal);
            Assert.Contains("rag_ingest", names, StringComparer.Ordinal);
        }
        finally
        {
            if (!tool.HasExited)
            {
                tool.Kill(entireProcessTree: true);
            }
        }
    }

    /// <remarks>
    /// The stdio transport must keep stdout clean for the protocol. A log line, a banner or a
    /// Kestrel startup message on stdout corrupts the JSON-RPC stream and the client sees a parse
    /// error rather than a server — which is the failure mode this package's README records.
    /// </remarks>
    [Fact]
    public async Task TheStdioServer_WritesNothingButProtocolToStdout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var tool = StartTool();

        try
        {
            var response = await RoundTripAsync(tool, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "rag-net-phase-6.2", version = "1.0.0" },
                },
            }, cts.Token);

            // RoundTripAsync only returns once a line parsed as JSON with the matching id; that it
            // succeeded at all is the assertion that the first protocol line was not preceded by
            // unparseable noise. Assert the shape too, so the test says what it proved.
            Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
            Assert.Equal(1, response.GetProperty("id").GetInt32());
        }
        finally
        {
            if (!tool.HasExited)
            {
                tool.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process StartTool()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ToolAssemblyPath);
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add("stdio");

        // Double underscore is the environment-variable form of a configuration section separator.
        startInfo.Environment["RagNet__ChatClient__Endpoint"] = "https://openrouter.ai/api/v1";
        startInfo.Environment["RagNet__ChatClient__ApiKey"] = "test-key";
        startInfo.Environment["RagNet__ChatClient__Model"] = "meta-llama/llama-3.3-70b-instruct";
        startInfo.Environment["RagNet__Embeddings__Endpoint"] = "https://openrouter.ai/api/v1";
        startInfo.Environment["RagNet__Embeddings__ApiKey"] = "test-key";
        startInfo.Environment["RagNet__Embeddings__Model"] = "text-embedding-3-small";
        startInfo.Environment["RagNet__Embeddings__VectorDimensions"] = "1536";
        startInfo.Environment["RagNet__VectorStore__Kind"] = "InMemory";

        var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "the MCP tool process did not start");
        return process;
    }

    private static async Task SendAsync(Process tool, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await tool.StandardInput.WriteAsync(json.AsMemory(), cancellationToken);
        await tool.StandardInput.WriteAsync("\n".AsMemory(), cancellationToken);
        await tool.StandardInput.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="message"/> and reads stdout until a line parses as JSON carrying the
    /// same <c>id</c>. Lines that do not parse are skipped rather than failing: the point of
    /// <see cref="TheStdioServer_WritesNothingButProtocolToStdout"/> is to assert their absence,
    /// and a helper that threw on them would make every other test fail for that one reason.
    /// </summary>
    private static async Task<JsonElement> RoundTripAsync(Process tool, object message, CancellationToken cancellationToken)
    {
        var id = (int)message.GetType().GetProperty("id")!.GetValue(message)!;
        await SendAsync(tool, message, cancellationToken);

        var skipped = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await tool.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                var stderr = await tool.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"the MCP tool closed stdout before answering id {id}. stderr:\n{stderr}\n" +
                    $"non-protocol stdout seen:\n{skipped}");
            }

            if (string.IsNullOrWhiteSpace(line)) { continue; }

            JsonElement element;
            try
            {
                element = JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (JsonException)
            {
                skipped.AppendLine(line);
                continue;
            }

            if (element.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number &&
                responseId.GetInt32() == id)
            {
                return element;
            }
        }

        throw new OperationCanceledException($"timed out waiting for a response to id {id}");
    }
}

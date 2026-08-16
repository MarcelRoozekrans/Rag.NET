using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Mcp.DependencyInjection;
using Xunit;

namespace Rag.NET.Mcp.Tests.Integration;

/// <summary>
/// Resolves the tool surface from a <b>real</b> MCP server registration, rather than constructing
/// <c>RagMcpTools</c> and calling its methods.
/// </summary>
/// <remarks>
/// <para>
/// <b>Milestone 6, Phase 6.2 (§2(d) of the phase design).</b> <c>RagMcpToolsTests</c> does
/// <c>new RagMcpTools(substitute)</c> and invokes the methods directly. That tests the method
/// bodies and nothing else. Everything between an MCP client and those bodies —
/// <c>AddMcpServer().WithTools&lt;RagMcpTools&gt;()</c> reflecting over the
/// <c>[McpServerToolType]</c> and <c>[McpServerTool]</c> attributes, the names it derives, the
/// schema it builds from the parameters, and the DI registration that makes the tools resolvable —
/// is skipped by a direct call and is exactly where a tool silently fails to be exposed.
/// </para>
/// <para>
/// A tool whose attribute was removed, renamed, or whose type stopped being discovered would keep
/// every assertion in <c>RagMcpToolsTests</c> green while disappearing from every client. This test
/// is the one that fails.
/// </para>
/// <para>
/// Complements <c>Rag.NET.Mcp.Tool.Tests/Integration/StdioTransportTests</c>, which proves the same
/// three tools survive a real process boundary and a real stdio JSON-RPC handshake. This one is
/// in-process and asserts the schema, which the transport test cannot reach without a model.
/// </para>
/// </remarks>
public sealed class RealMcpServerTests
{
    [Fact]
    public async Task TheRealRegistration_DiscoversAllThreeTools()
    {
        await using var provider = BuildProvider();

        var names = (await ResolveToolsAsync(provider))
            .Select(t => t.ProtocolTool.Name)
            .ToList();

        Assert.Contains("rag_retrieve", names, StringComparer.Ordinal);
        Assert.Contains("rag_ask", names, StringComparer.Ordinal);
        Assert.Contains("rag_ingest", names, StringComparer.Ordinal);
    }

    /// <remarks>
    /// The description is what a client shows a model when it decides whether to call the tool. An
    /// empty one is not a compile error and not a test failure anywhere else in this project.
    /// </remarks>
    [Fact]
    public async Task EveryDiscoveredTool_CarriesADescription()
    {
        await using var provider = BuildProvider();

        foreach (var tool in await ResolveToolsAsync(provider))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(tool.ProtocolTool.Description),
                $"tool '{tool.ProtocolTool.Name}' has no description");
        }
    }

    /// <remarks>
    /// The input schema is generated from the method's parameters by the SDK, not written by hand.
    /// This asserts the generation actually happened and named the required parameter — the seam
    /// where a renamed parameter silently changes the wire contract.
    /// </remarks>
    [Fact]
    public async Task TheRetrieveTool_PublishesAQueryParameterInItsSchema()
    {
        await using var provider = BuildProvider();

        var retrieve = Assert.Single(
            await ResolveToolsAsync(provider),
            t => string.Equals(t.ProtocolTool.Name, "rag_retrieve", StringComparison.Ordinal));

        var schema = retrieve.ProtocolTool.InputSchema.ToString();
        Assert.Contains("query", schema, StringComparison.Ordinal);
        Assert.Contains("topK", schema, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRagPipeline>());
        services.AddRagNetMcpServer();
        return services.BuildServiceProvider();
    }

    private static async Task<IList<McpServerTool>> ResolveToolsAsync(IServiceProvider provider)
    {
        // The tools the real registration produced, as the server itself would enumerate them.
        var tools = provider.GetServices<McpServerTool>().ToList();
        Assert.NotEmpty(tools);
        await Task.CompletedTask;
        return tools;
    }
}

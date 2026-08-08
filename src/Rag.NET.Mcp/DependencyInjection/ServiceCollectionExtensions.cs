using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Mcp.Tools;

namespace Rag.NET.Mcp.DependencyInjection;

/// <summary>Extension methods for registering the Rag.NET MCP server.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Rag.NET MCP server and its tools with the DI container.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>A <see cref="McpServerBuilder"/> for further configuration.</returns>
    public static McpServerBuilder AddRagNetMcpServer(this IServiceCollection services)
    {
        var server = services.AddMcpServer()
                              .WithTools<RagMcpTools>();

        return new McpServerBuilder(services, server);
    }
}

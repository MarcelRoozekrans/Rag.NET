using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Mcp.DependencyInjection;

/// <summary>Fluent builder for configuring the Rag.NET MCP server.</summary>
/// <param name="services">The service collection <see cref="AddRagNetMcpServer"/> was called on.</param>
/// <param name="server">
/// The MCP SDK builder returned by the underlying <c>AddMcpServer()</c> call.
/// </param>
public sealed class McpServerBuilder(IServiceCollection services, IMcpServerBuilder server)
{
    /// <summary>Gets the underlying <see cref="IServiceCollection"/> for advanced registrations.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// Gets the MCP SDK's own builder, for transports this package cannot wrap directly.
    /// </summary>
    /// <remarks>
    /// <c>Rag.NET.Mcp</c> deliberately references only the <c>ModelContextProtocol</c>
    /// package, not <c>ModelContextProtocol.AspNetCore</c> — taking an ASP.NET Core dependency
    /// here would force it on every consumer that hosts MCP tools in a non-web process (see the
    /// design's §1.3). That means the real <c>WithHttpTransport(...)</c> extension, which lives
    /// in the ASP.NET Core package, is not reachable from this type. A caller that references
    /// <c>ModelContextProtocol.AspNetCore</c> — such as <c>Rag.NET.Mcp.Tool</c> — configures HTTP
    /// by calling <c>Server.WithHttpTransport(...)</c> directly; see that project's
    /// <c>Program.cs</c> for an example. There is intentionally no <c>WithHttpTransport</c>
    /// wrapper on this builder: one existed before and silently did nothing, because it had
    /// nothing real to call.
    /// </remarks>
    public IMcpServerBuilder Server { get; } = server;

    /// <summary>Configures the MCP server to use the stdio transport.</summary>
    public McpServerBuilder WithStdioTransport()
    {
        Server.WithStdioServerTransport();
        return this;
    }
}

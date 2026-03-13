using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Mcp.DependencyInjection;

/// <summary>Fluent builder for configuring the Rag.NET MCP server.</summary>
public sealed class McpServerBuilder(IServiceCollection services)
{
    internal McpTransportOptions Options { get; } = new();

    /// <summary>Gets the underlying <see cref="IServiceCollection"/> for advanced registrations.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>Configures the MCP server to use the stdio transport.</summary>
    public McpServerBuilder WithStdioTransport()
    {
        Options.UseStdio = true;
        return this;
    }

    /// <summary>Configures the MCP server to use an HTTP (SSE) transport on the specified <paramref name="port"/>.</summary>
    /// <param name="port">The TCP port to listen on (default: 5050).</param>
    public McpServerBuilder WithHttpTransport(int port = 5050)
    {
        Options.UseHttp = true;
        Options.HttpPort = port;
        return this;
    }

    /// <summary>Sets the API key clients must supply when connecting over HTTP.</summary>
    /// <param name="key">The shared secret key.</param>
    public McpServerBuilder WithApiKey(string key)
    {
        Options.ApiKey = key;
        return this;
    }
}

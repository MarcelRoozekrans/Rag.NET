namespace Rag.NET.Mcp.DependencyInjection;

/// <summary>Transport configuration for the Rag.NET MCP server.</summary>
public sealed class McpTransportOptions
{
    /// <summary>When <see langword="true"/>, the server listens on standard I/O (stdio transport).</summary>
    public bool UseStdio { get; set; }

    /// <summary>When <see langword="true"/>, the server listens on HTTP (SSE transport).</summary>
    public bool UseHttp { get; set; }

    /// <summary>Port used when <see cref="UseHttp"/> is <see langword="true"/>.</summary>
    public int HttpPort { get; set; } = 5050;

    /// <summary>Optional API key required by clients when connecting over HTTP.</summary>
    public string? ApiKey { get; set; }
}

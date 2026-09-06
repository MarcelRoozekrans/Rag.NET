using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Mcp.DependencyInjection;

namespace Rag.NET.Mcp.AspNetCore;

/// <summary>
/// Adds the guarded HTTP transport to a <see cref="McpServerBuilder"/>.
/// </summary>
/// <remarks>
/// This is the wrapper <c>Rag.NET.Mcp</c> cannot provide. That package references only
/// <c>ModelContextProtocol</c>, deliberately, so stdio consumers are not made to carry a web
/// framework — and its own <c>McpServerBuilder.Server</c> remarks record that a
/// <c>WithHttpTransport</c> wrapper once existed there and <i>silently did nothing, because it had
/// nothing real to call</i>. Here there is something real to call.
/// </remarks>
public static class McpServerBuilderExtensions
{
    /// <summary>
    /// Registers the MCP Streamable HTTP transport together with the authentication decision that
    /// <see cref="McpEndpointRouteBuilderExtensions.MapRagNetMcp"/> will enforce.
    /// </summary>
    /// <param name="builder">The Rag.NET MCP builder.</param>
    /// <param name="configure">
    /// Sets the API key, or opts out explicitly. Required: an unconfigured delegate leaves the
    /// decision unmade, and this method throws rather than choosing for you.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Neither <see cref="RagNetMcpHttpOptions.ApiKey"/> nor
    /// <see cref="RagNetMcpHttpOptions.AllowAnonymous"/> was set.
    /// </exception>
    /// <remarks>
    /// <b>The throw is at configuration time on purpose.</b> This is the earliest moment the
    /// decision is knowable, so the process fails at startup rather than on the first
    /// unauthenticated request — by which time the endpoint has already been reachable. It is the
    /// same posture <c>ragnet-mcp</c> takes at <c>Program.cs:51</c>, moved from the tool into the
    /// library so that consumers building their own host get it too.
    /// </remarks>
    public static McpServerBuilder WithRagNetHttpTransport(
        this McpServerBuilder builder,
        Action<RagNetMcpHttpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RagNetMcpHttpOptions();
        configure?.Invoke(options);

        if (string.IsNullOrEmpty(options.ApiKey) && !options.AllowAnonymous)
        {
            throw new InvalidOperationException(
                "The Rag.NET MCP HTTP transport has no authentication configured. This surface " +
                "exposes rag_ingest as well as retrieve and ask, so serving it unauthenticated " +
                "lets anyone who can reach the port write into the corpus your answers come from " +
                "-- and retrieval degrades silently, so nothing would surface it. Set " +
                $"{nameof(RagNetMcpHttpOptions)}.{nameof(RagNetMcpHttpOptions.ApiKey)}, or set " +
                $"{nameof(RagNetMcpHttpOptions.AllowAnonymous)} = true if the host really is " +
                "behind a gateway that authenticates for it.");
        }

        builder.Services.AddSingleton(options);
        builder.Server.WithHttpTransport();

        return builder;
    }
}

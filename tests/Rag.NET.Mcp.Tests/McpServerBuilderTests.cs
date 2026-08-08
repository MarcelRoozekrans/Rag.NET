using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using Rag.NET.Mcp.DependencyInjection;
using Xunit;

namespace Rag.NET.Mcp.Tests;

/// <summary>
/// Regression coverage for the transport registration bug: <c>WithStdioTransport()</c> and
/// <c>WithHttpTransport(port)</c> used to only set fields on an <c>McpTransportOptions</c>
/// singleton that nothing read, so no MCP SDK transport was ever registered. Every assertion here
/// inspects the <see cref="IServiceCollection"/> for what the MCP SDK itself registered --
/// asserting our own flag was exactly what let that bug ship unnoticed.
/// </summary>
public sealed class McpServerBuilderTests
{
    [Fact]
    public void AddRagNetMcpServer_ExposesTheUnderlyingSdkBuilder()
    {
        var services = new ServiceCollection();

        var builder = services.AddRagNetMcpServer();

        Assert.NotNull(builder.Server);
        Assert.Same(services, builder.Services);
    }

    [Fact]
    public void AddRagNetMcpServer_WithoutConfiguringATransport_RegistersNoSdkTransport()
    {
        // Documents the failure this task fixes: stdio is the default, and every MCP client uses
        // it, but until a transport is explicitly requested the SDK must not have wired anything.
        // The old bug's stdio side was the opposite of this: it silently started a bare Kestrel
        // server on port 5000 because nothing had told the SDK to listen on stdio *or* HTTP.
        var services = new ServiceCollection();

        services.AddRagNetMcpServer();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ITransport));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void WithStdioTransport_RegistersTheSdksRealStdioTransport()
    {
        var services = new ServiceCollection();

        services.AddRagNetMcpServer().WithStdioTransport();

        // ModelContextProtocol.Protocol.ITransport is the SDK's own transport abstraction; it is
        // only registered once a transport extension (stdio or HTTP) actually runs.
        Assert.Contains(services, d => d.ServiceType == typeof(ITransport));

        // The SDK also registers the hosted service that pumps stdio messages. Its type is
        // internal to the SDK, so it is matched by name rather than referenced directly -- the
        // name itself ("SingleSessionMcpServerHostedService") is what distinguishes stdio's
        // registration from HTTP's ("IdleTrackingBackgroundService", asserted below).
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService) &&
            string.Equals(
                d.ImplementationType?.FullName,
                "ModelContextProtocol.SingleSessionMcpServerHostedService",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Server_CanReachTheRealAspNetCoreHttpTransportExtension()
    {
        // Rag.NET.Mcp deliberately does not reference ModelContextProtocol.AspNetCore (design
        // §1.3), so WithHttpTransport() cannot live on McpServerBuilder itself -- that is exactly
        // what produced the original no-op. This proves the exposed Server property is genuinely
        // the SDK's own IMcpServerBuilder: a caller that *does* reference the ASP.NET Core
        // package (as Rag.NET.Mcp.Tool does) can call the real extension straight through it.
        var services = new ServiceCollection();
        var builder = services.AddRagNetMcpServer();

        builder.Server.WithHttpTransport();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService) &&
            string.Equals(
                d.ImplementationType?.FullName,
                "ModelContextProtocol.AspNetCore.IdleTrackingBackgroundService",
                StringComparison.Ordinal));
    }
}

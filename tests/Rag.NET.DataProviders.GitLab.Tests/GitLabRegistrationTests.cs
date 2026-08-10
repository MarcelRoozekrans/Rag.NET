using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Rag.NET.DataProviders.GitLab.Tests;

/// <summary>
/// <c>AddGitLabDataProvider</c> constructed its <c>GitLabClient</c> from the raw <c>baseUrl</c>
/// parameter before the configure callback even ran, so <see cref="GitLabOptions.BaseUrl"/> was
/// never read and a callback changing it was silently ignored (issue #108). NGitLab exposes no
/// public view of the host it targets, so this test observes the only honest way: a loopback
/// HTTP server plays the GitLab API, the callback points <c>BaseUrl</c> at it, and the raw
/// parameter points at a dead port — traffic arriving at the server proves the callback won.
/// </summary>
public sealed class GitLabRegistrationTests
{
    [Fact]
    public async Task ConfigureCallback_ChangingBaseUrl_TargetsTheNewInstance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = new HttpListener();
        var port = ReserveFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var observedPaths = new List<string>();
        var serveTask = ServeEmptyJsonAsync(listener, observedPaths, cancellationToken);

        var services = new ServiceCollection();
        services.AddGitLabDataProvider(
            // Dead port: only reachable if the configure callback is ignored — connecting here
            // fails fast, which is exactly what the pre-fix wiring would do.
            baseUrl: "http://127.0.0.1:1",
            projectIdOrPath: "42",
            token: "glpat-test",
            configure: o => o.BaseUrl = $"http://127.0.0.1:{port}");

        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileContentProvider>();

        var results = await provider.GetFilesAsync(cancellationToken)
            .ToListAsync(cancellationToken);

        listener.Stop();
        await serveTask;

        Assert.Empty(results); // the fake API returned an empty tree
        Assert.Contains(observedPaths, p => p.Contains("/projects/42/repository/tree", StringComparison.Ordinal));
    }

    private static async Task ServeEmptyJsonAsync(
        HttpListener listener, List<string> observedPaths, CancellationToken cancellationToken)
    {
        try
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                observedPaths.Add(context.Request.Url?.PathAndQuery ?? string.Empty);

                var body = "[]"u8.ToArray();
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body, cancellationToken);
                context.Response.Close();
            }
        }
        catch (HttpListenerException)
        {
            // Listener stopped — normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Listener disposed — normal shutdown.
        }
    }

    private static int ReserveFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }
}

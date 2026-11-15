using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Rag.NET.DataProviders.Web.Tests;

/// <summary>
/// A loopback HTTP server that sends the status line, the headers and part of the body of every
/// response, then stops sending without closing the connection. A stalled server looks like this
/// to the client: the headers arrived, and the rest of <c>Content-Length</c> never does (#1009).
/// </summary>
internal sealed class StalledBodyServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _accepting;

    public StalledBodyServer(string bodyPrefix = "User-agent: *\n")
    {
        _listener.Start();
        _accepting = AcceptAsync(bodyPrefix);
    }

    public Uri BaseUri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    private async Task AcceptAsync(string bodyPrefix)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                lock (_clients) _clients.Add(client);
                _ = RespondAsync(client, bodyPrefix);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private async Task RespondAsync(TcpClient client, string bodyPrefix)
    {
        try
        {
            var stream = client.GetStream();
            var request = new StringBuilder();
            var buffer = new byte[1024];
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (n == 0) return;
                request.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }

            var body = Encoding.UTF8.GetBytes(bodyPrefix);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length + 100}\r\n\r\n");
            await stream.WriteAsync(head, _stop.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
            await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            // Disposed, or the client gave up.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        lock (_clients)
        {
            foreach (var client in _clients) client.Dispose();
        }

        await _accepting.ConfigureAwait(false);
        _stop.Dispose();
    }
}


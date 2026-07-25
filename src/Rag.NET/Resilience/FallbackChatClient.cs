using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Rag.NET.Resilience;

/// <summary>
/// An <see cref="IChatClient"/> decorator that falls back to subsequent clients
/// when the current one raises a transient error (rate-limit, timeout, service-unavailable).
/// Non-transient errors propagate immediately without consulting further clients.
/// </summary>
/// <remarks>
/// When <paramref name="perClientTimeout"/> is set, each per-client attempt is bounded:
/// the client call runs under a token linked to the caller's token with
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> armed. A timeout
/// (linked token fired while the caller's token is not cancelled) is treated as a
/// transient failure and the next client is tried; caller cancellation always
/// propagates immediately. For streaming, the timeout spans the whole per-client
/// attempt (first token through stream completion), not just time-to-first-token.
/// </remarks>
public sealed class FallbackChatClient(
    IReadOnlyList<IChatClient> clients,
    ILogger<FallbackChatClient>? logger = null,
    TimeSpan? perClientTimeout = null) : IChatClient
{
    private readonly IReadOnlyList<IChatClient> _clients = clients.Count > 0
        ? clients
        : throw new ArgumentOutOfRangeException(nameof(clients), "At least one client is required.");

    private readonly TimeSpan? _perClientTimeout = perClientTimeout is null || perClientTimeout > TimeSpan.Zero
        ? perClientTimeout
        : throw new ArgumentOutOfRangeException(nameof(perClientTimeout), perClientTimeout,
            "Per-client timeout must be greater than zero when set.");

    private static readonly string[] s_transientKeywords = ["rate limit", "throttl", "timeout", "unavailable"];

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        for (int i = 0; i < _clients.Count; i++)
        {
            try
            {
                return await GetResponseWithTimeoutAsync(_clients[i], messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (_perClientTimeout is not null && !cancellationToken.IsCancellationRequested)
            {
                // The linked per-client timeout fired, not the caller: transient → next client.
                last = ex;
                if (i < _clients.Count - 1)
                    logger?.LogWarning(ex, "Client {Index} timed out after {Timeout}; trying next client.",
                        i.ToString(CultureInfo.InvariantCulture), _perClientTimeout.Value.ToString("c", CultureInfo.InvariantCulture));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                if (i < _clients.Count - 1)
                    logger?.LogWarning(ex, "Client {Index} failed transiently; trying next client.", i.ToString(CultureInfo.InvariantCulture));
            }
        }
        throw last!;
    }

    private async Task<ChatResponse> GetResponseWithTimeoutAsync(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CreateTimeoutCts(cancellationToken);
        return await client.GetResponseAsync(messages, options, timeoutCts?.Token ?? cancellationToken).ConfigureAwait(false);
    }

    private CancellationTokenSource? CreateTimeoutCts(CancellationToken cancellationToken)
    {
        if (_perClientTimeout is not { } timeout)
            return null;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        for (int i = 0; i < _clients.Count; i++)
        {
            var state = new StreamState();
            await foreach (var update in TryStreamClientAsync(i, messages, options, cancellationToken, state).ConfigureAwait(false))
                yield return update;

            if (state.TransientException is not null)
            {
                last = state.TransientException;
                continue;
            }

            yield break;
        }

        throw last!;
    }

    private async IAsyncEnumerable<ChatResponseUpdate> TryStreamClientAsync(
        int clientIndex,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        StreamState state)
    {
        using var timeoutCts = CreateTimeoutCts(cancellationToken);
        var effectiveToken = timeoutCts?.Token ?? cancellationToken;

        var enumerator = _clients[clientIndex]
            .GetStreamingResponseAsync(messages, options, effectiveToken)
            .GetAsyncEnumerator(effectiveToken);

        try
        {
            int itemsYielded = 0;
            bool? hasNext = await MoveNextOrClassifyAsync(enumerator, clientIndex, itemsYielded, timeoutCts is not null, cancellationToken, state).ConfigureAwait(false);
            while (hasNext == true)
            {
                yield return enumerator.Current;
                itemsYielded++;
                hasNext = await MoveNextOrClassifyAsync(enumerator, clientIndex, itemsYielded, timeoutCts is not null, cancellationToken, state).ConfigureAwait(false);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Advances the stream. Returns <see langword="true"/>/<see langword="false"/> for a normal
    /// move, or <see langword="null"/> when a transient failure (including a per-client timeout)
    /// was recorded in <paramref name="state"/>; caller cancellation and non-transient
    /// exceptions propagate.
    /// </summary>
    private async ValueTask<bool?> MoveNextOrClassifyAsync(
        IAsyncEnumerator<ChatResponseUpdate> enumerator,
        int clientIndex,
        int itemsYielded,
        bool timeoutArmed,
        CancellationToken callerToken,
        StreamState state)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutArmed && !callerToken.IsCancellationRequested)
        {
            // The linked per-client timeout fired, not the caller: transient → next client.
            state.TransientException = ex;
            LogStreamFailure(clientIndex, itemsYielded, ex);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsTransient(ex))
        {
            state.TransientException = ex;
            LogStreamFailure(clientIndex, itemsYielded, ex);
            return null;
        }
    }

    private void LogStreamFailure(int clientIndex, int itemsYielded, Exception ex)
    {
        if (clientIndex >= _clients.Count - 1)
            return;

        if (itemsYielded == 0)
            logger?.LogWarning(ex, "Streaming client {Index} failed before first token; trying next client.", clientIndex.ToString(CultureInfo.InvariantCulture));
        else
            logger?.LogWarning(ex, "Streaming client {Index} failed mid-stream after {Count} token(s); restarting with next client.",
                clientIndex.ToString(CultureInfo.InvariantCulture), itemsYielded.ToString(CultureInfo.InvariantCulture));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        foreach (var client in _clients)
        {
            var svc = client.GetService(serviceType, serviceKey);
            if (svc is not null) return svc;
        }
        return null;
    }

    public void Dispose() { /* clients are externally owned */ }

    internal static bool IsTransient(Exception ex)
    {
        if (ex is OperationCanceledException or TaskCanceledException or TimeoutException)
            return true;

        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is null or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                return true;
        }

        var msg = ex.Message;
        foreach (var keyword in s_transientKeywords)
        {
            if (msg.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private sealed class StreamState
    {
        public Exception? TransientException { get; set; }
    }
}

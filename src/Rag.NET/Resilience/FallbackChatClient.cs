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
public sealed class FallbackChatClient(
    IReadOnlyList<IChatClient> clients,
    ILogger<FallbackChatClient>? logger = null) : IChatClient
{
    private static readonly string[] s_transientKeywords = ["rate limit", "throttl", "timeout", "unavailable"];

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        for (int i = 0; i < clients.Count; i++)
        {
            try
            {
                return await clients[i].GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                if (i < clients.Count - 1)
                    logger?.LogWarning(ex, "Client {Index} failed transiently ({Message}); trying next client.", i.ToString(CultureInfo.InvariantCulture), ex.Message);
            }
        }
        throw last!;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        for (int i = 0; i < clients.Count; i++)
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
        var enumerator = clients[clientIndex]
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        bool hasNext;
        try
        {
            hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsTransient(ex))
        {
            state.TransientException = ex;
            if (clientIndex < clients.Count - 1)
                logger?.LogWarning(ex, "Streaming client {Index} failed transiently; trying next client.", clientIndex.ToString(CultureInfo.InvariantCulture));
            yield break;
        }

        while (hasNext)
        {
            yield return enumerator.Current;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsTransient(ex))
            {
                state.TransientException = ex;
                if (clientIndex < clients.Count - 1)
                    logger?.LogWarning(ex, "Streaming client {Index} failed mid-stream; trying next client.", clientIndex.ToString(CultureInfo.InvariantCulture));
                yield break;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        clients[0].GetService(serviceType, serviceKey);

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

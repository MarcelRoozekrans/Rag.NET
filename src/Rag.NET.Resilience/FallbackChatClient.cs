using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Models;

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
/// When every client fails and the last failure was a per-client timeout, a
/// <see cref="TimeoutException"/> (wrapping the last cancellation) is thrown so a
/// total provider outage is not mistaken for caller cancellation.
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
            using var timeoutCts = CreateTimeoutCts(cancellationToken);
            try
            {
                return await _clients[i].GetResponseAsync(messages, options, timeoutCts?.Token ?? cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (IsPerClientTimeout(timeoutCts, cancellationToken))
            {
                // The linked per-client timeout actually fired, not the caller: transient → next client.
                last = ex;
                if (i < _clients.Count - 1)
                    logger?.LogWarning(ex, "Client {Index} timed out after {Timeout}; trying next client.",
                        i.ToString(CultureInfo.InvariantCulture), _perClientTimeout!.Value.ToString("c", CultureInfo.InvariantCulture));
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
        throw FinalException(last!, cancellationToken);
    }

    private CancellationTokenSource? CreateTimeoutCts(CancellationToken cancellationToken)
    {
        if (_perClientTimeout is not { } timeout)
            return null;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>
    /// True only when the per-client timeout CTS actually fired while the caller's token
    /// did not: a spontaneous client OCE (neither token cancelled) is NOT a timeout and
    /// falls through to the rethrow path, matching no-timeout behavior.
    /// </summary>
    private static bool IsPerClientTimeout(CancellationTokenSource? timeoutCts, CancellationToken callerToken) =>
        timeoutCts is not null && timeoutCts.IsCancellationRequested && !callerToken.IsCancellationRequested;

    /// <summary>
    /// Wraps an all-clients-exhausted cancellation in a <see cref="TimeoutException"/> when it
    /// stems from the per-client timeout rather than the caller: a total provider outage must
    /// not masquerade as caller cancellation (e.g. ASP.NET treats OCE as client disconnect).
    /// </summary>
    private Exception FinalException(Exception last, CancellationToken cancellationToken)
    {
        if (last is OperationCanceledException && !cancellationToken.IsCancellationRequested && _perClientTimeout is { } timeout)
        {
            return new TimeoutException(
                string.Create(CultureInfo.InvariantCulture,
                    $"All {_clients.Count} clients failed; the last attempt exceeded the per-client timeout of {timeout:c}."),
                last);
        }

        return last;
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

        throw FinalException(last!, cancellationToken);
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
            bool? hasNext = await MoveNextOrClassifyAsync(enumerator, clientIndex, itemsYielded, timeoutCts, cancellationToken, state).ConfigureAwait(false);
            while (hasNext == true)
            {
                yield return enumerator.Current;
                itemsYielded++;
                hasNext = await MoveNextOrClassifyAsync(enumerator, clientIndex, itemsYielded, timeoutCts, cancellationToken, state).ConfigureAwait(false);
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
        CancellationTokenSource? timeoutCts,
        CancellationToken callerToken,
        StreamState state)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (IsPerClientTimeout(timeoutCts, callerToken))
        {
            // The linked per-client timeout actually fired, not the caller: transient → next client.
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

    /// <summary>
    /// Answers for its own type first (so a stacked decorator chain is probeable layer by
    /// layer), then asks each chained client in order.
    /// </summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        foreach (var client in _clients)
        {
            var svc = client.GetService(serviceType, serviceKey);
            if (svc is not null) return svc;
        }
        return null;
    }

    public void Dispose() { /* clients are externally owned */ }

    /// <summary>
    /// Reports whether an exception is the OpenAI SDK failing to deserialise a completion whose
    /// <c>finish_reason</c> the provider set to something outside the OpenAI schema — in practice
    /// <c>"error"</c>, which OpenRouter and other compatible gateways emit when the upstream model
    /// call fails.
    /// <para>
    /// <b>This is a provider outage wearing the wrong exception type.</b> The throw happens inside
    /// <c>ChatFinishReasonExtensions.ToChatFinishReason</c> during response deserialisation, before
    /// any of this library sees the response, and it surfaces as a bare
    /// <see cref="ArgumentOutOfRangeException"/> — a shape that reads as a caller bug. Nothing in
    /// <see cref="IsTransient"/> matched it, so the one failure fallback exists for did not trigger
    /// fallback: it propagated, and in ingestion the catch-all turned it into
    /// <c>RagError.StorageFailed</c>, pointing a reader at the vector store (issue #143, observed
    /// three times in one day).
    /// </para>
    /// <para>
    /// <b>Matched on the message, against this file's own preference for matching on type.</b> The
    /// SDK throws the framework exception with no subclass, no error code and no data, so the type
    /// carries nothing to key on and treating every <see cref="ArgumentOutOfRangeException"/> as
    /// transient would retry genuine programming errors against every configured provider in turn.
    /// The parameter name and the type it failed to parse are the narrowest signature available,
    /// and both are part of the SDK's observable behaviour rather than prose that reads as
    /// incidental.
    /// </para>
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns>Whether it is a provider-side error response rather than a caller mistake.</returns>
    internal static bool IsProviderErrorResponse(Exception ex) =>
        ex is ArgumentOutOfRangeException
        && ex.Message.Contains("ChatFinishReason", StringComparison.Ordinal);

    internal static bool IsTransient(Exception ex)
    {
        // A blown budget must NEVER trigger provider fallback — retrying against the next
        // client would keep spending past the limit. Pinned by type (not message wording)
        // so the exception text can evolve freely.
        if (ex is BudgetExceededException)
            return false;

        if (ex is OperationCanceledException or TaskCanceledException or TimeoutException)
            return true;

        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is null or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                return true;
        }

        if (IsProviderErrorResponse(ex))
            return true;

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

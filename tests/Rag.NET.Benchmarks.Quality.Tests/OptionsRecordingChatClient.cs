using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// A chat client that records the <see cref="ChatOptions"/> it was called with and answers with a
/// fixed, settable reply.
/// </summary>
/// <remarks>
/// Built for <see cref="CachedGraphRagClientOptionsTests"/>, where the whole point of the test is
/// what <see cref="CachedGraphRagClient"/> hands its inner client — <see cref="Received"/> is the
/// only way to see that from outside. <see cref="Reply"/> is settable rather than fixed at
/// construction so one test can change what the "model" says between two calls through the same
/// client, which is how a second, differently-keyed cache entry is told apart from a replayed
/// first one.
/// </remarks>
internal sealed class OptionsRecordingChatClient : IChatClient
{
    public OptionsRecordingChatClient(string reply)
    {
        Reply = reply;
    }

    /// <summary>Gets or sets the text every call answers with.</summary>
    public string Reply { get; set; }

    /// <summary>Gets the options the most recent call was made with.</summary>
    public ChatOptions? Received { get; private set; }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        Received = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply)));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by these tests.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to release.
    }
}

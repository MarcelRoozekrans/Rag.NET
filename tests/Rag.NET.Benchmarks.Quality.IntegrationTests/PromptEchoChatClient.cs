using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// A chat client that answers every request with a bounded head of the prompt it was given, and
/// counts the calls. <see cref="GraphRagFunctionsTests"/> hands one to community-report generation
/// and one to global search's map-reduce.
/// <para>
/// <b>Neither of those two is cached, and they are uncached for different reasons — both worth
/// knowing.</b> Entity extraction is cached and replayed refuse-on-miss, because what it returns
/// decides what the graph contains and must not vary between runs.
/// </para>
/// <para>
/// <b>Community reports cannot be generated at all on this graph, and that is a measurement.</b>
/// <c>CommunityDetectionBehavior</c> builds a report prompt by pasting every member entity's whole
/// merged description into one message, with no bound of any kind. Over the sixty-article slice
/// Leiden puts 8,070 of 8,999 entities into a single community, and that one prompt measures
/// <b>1,806,352 characters</b> — roughly 450,000 tokens, against gpt-4o-mini's 128,000-token
/// context. There is no model to send it to. Generating the reports is therefore not something this
/// guard declined to pay for; it is something the library cannot currently do, and the finding is
/// recorded here rather than hidden behind a skip.
/// </para>
/// <para>
/// The figure is <see cref="LongestPrompt"/>, printed by every run, rather than a number somebody
/// once observed — an earlier note here put it at 976,425 characters, which was the entity-
/// description block alone and roughly half of what the behavior actually sends.
/// </para>
/// <para>
/// <b>Global search's map-reduce is uncached because its prompts are machine-dependent.</b> They
/// are built from whichever community reports the vector search returned, and ONNX Runtime
/// dispatches its kernels on the available instruction set — a vector can differ in its last bits
/// and a near-tie can order the other way. Caching those prompts would make this guard fail
/// refuse-on-miss on every machine but the one that recorded them, for a reason with nothing to do
/// with GraphRAG. Nothing asserted about global search depends on the text either: what is checked
/// is that reports were found, partitioned, batched and reduced into a result set that differs from
/// local search's, every step of which is the behavior's own.
/// </para>
/// <para>
/// <b>Echoing the prompt rather than returning a constant is deliberate.</b> A constant would give
/// all 655 community reports identical text, identical vectors and identical scores — they would
/// flood or vanish from every candidate set as one block, and "the query retrieved a community
/// report" would stop meaning anything. The head of the prompt is that community's own entity
/// descriptions, so reports stay distinct and stay about their community, without pretending to be
/// a summary a model wrote.
/// </para>
/// </summary>
internal sealed class PromptEchoChatClient : IChatClient
{
    /// <summary>
    /// How much of the prompt comes back. Roughly a long paragraph — the shape of the report
    /// <c>CommunityDetectionBehavior</c> asks for ("2-4 paragraphs"), and small enough that the
    /// giant community's million-character prompt does not become a million-character chunk.
    /// </summary>
    private const int MaxEchoLength = 2000;

    private long _calls;
    private long _longestPrompt;

    /// <summary>Gets how many requests have been answered.</summary>
    public long Calls => Interlocked.Read(ref _calls);

    /// <summary>Gets the character length of the longest prompt this client was ever handed.</summary>
    /// <remarks>
    /// <b>Measured on every run rather than quoted from one.</b> The million-character report prompt
    /// described above is the single most consequential number about this pipeline — it is what
    /// makes community reports ungeneratable against any real model — and a figure that lives only
    /// in a comment cannot go red when it changes. Recording it here means the run prints what the
    /// prompt costs today, so a fix that shrinks it and a regression that regrows it are both
    /// visible.
    /// </remarks>
    public long LongestPrompt => Interlocked.Read(ref _longestPrompt);

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        _ = Interlocked.Increment(ref _calls);
        RecordPromptLength(messages);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Echo(messages))));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Nothing in GraphRAG streams, so this stub does not either.");

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

    /// <summary>Keeps the high-water mark of how much text one request carried.</summary>
    /// <remarks>
    /// Every message, not just the first: the report prompt is one message today, but a client that
    /// split its instructions from its entity block would otherwise report half of what it sends.
    /// </remarks>
    private void RecordPromptLength(IEnumerable<ChatMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            total += message.Text.Length;
        }

        long seen = Interlocked.Read(ref _longestPrompt);
        while (total > seen)
        {
            var previous = Interlocked.CompareExchange(ref _longestPrompt, total, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }

    /// <summary>
    /// The bounded head of the request's text, never empty.
    /// </summary>
    /// <remarks>
    /// Empty would be worse than useless: <c>GraphGlobalSearchBehavior.MapPhase</c> drops empty
    /// partial answers, so a blank reply would make the reduce phase see nothing and the behavior
    /// would look as though it had partitioned no reports at all.
    /// </remarks>
    private static string Echo(IEnumerable<ChatMessage> messages)
    {
        var text = string.Empty;
        foreach (var message in messages)
        {
            text = message.Text;
            break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "(the prompt carried no text)";
        }

        return text.Length <= MaxEchoLength ? text : text[..MaxEchoLength];
    }
}

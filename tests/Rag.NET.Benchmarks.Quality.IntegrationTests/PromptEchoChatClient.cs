using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// A chat client that answers every request with a bounded head of the prompt it was given, and
/// counts the calls. <see cref="GraphRagFunctionsTests"/> hands one to global search's map-reduce,
/// and to nothing else.
/// <para>
/// <b>Global search's map-reduce is uncached because its prompts are machine-dependent, and that is
/// now the only reason anything here is uncached.</b> They are built from whichever community
/// reports the vector search returned, and ONNX Runtime dispatches its kernels on the available
/// instruction set — a vector can differ in its last bits and a near-tie can order the other way.
/// Caching those prompts would make this guard fail refuse-on-miss on every machine but the one
/// that recorded them, for a reason with nothing to do with GraphRAG. Nothing asserted about global
/// search depends on the text either: what is checked is that reports were found, partitioned,
/// batched and reduced into a result set that differs from local search's, every step of which is
/// the behavior's own.
/// </para>
/// <para>
/// <b>Community reports used to come from here too, and no longer do (#172).</b> They were
/// synthesised rather than generated because <c>CommunityDetectionBehavior</c> pasted every member
/// entity's whole merged description into one unbounded message: while Leiden was discarding
/// intra-community weight it put 8,070 of 8,999 entities in one community and that prompt measured
/// <b>1,806,352 characters</b>, some 450,000 tokens against gpt-4o-mini's 128,000-token context.
/// Folding those edges into a self-loop took the largest community to 796 entities and the largest
/// prompt to 195,446; the Leiden paper's refinement phase (#180) took it to 661; and
/// <c>GraphRagOptions.MaxCommunityReportPromptLength</c> then bounded the prompt by the code rather
/// than by the corpus. What was impossible became one cheap generation run, so reports are now
/// generated once by the tool and replayed from <c>GraphExtractionCache</c> exactly as extractions
/// are — and the guard finally asserts against reports a model actually wrote.
/// </para>
/// <para>
/// <b>Echoing the prompt rather than returning a constant is deliberate.</b> A constant would give
/// every map call the same partial answer, so the reduce phase would be summarising one text
/// repeated — and "the map phase partitioned the reports it was handed" would stop meaning
/// anything. The head of the prompt is the batch's own reports, so each partial answer stays about
/// what it was given, without pretending to be prose a model wrote.
/// </para>
/// </summary>
internal sealed class PromptEchoChatClient : IChatClient
{
    /// <summary>
    /// How much of the prompt comes back. Roughly a long paragraph — the shape of the partial
    /// answer <c>GraphGlobalSearchBehavior</c> asks its map phase for, and small enough that a
    /// batch of long community reports does not come back as one enormous partial.
    /// </summary>
    private const int MaxEchoLength = 2000;

    private long _calls;

    /// <summary>Gets how many requests have been answered.</summary>
    public long Calls => Interlocked.Read(ref _calls);

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        _ = Interlocked.Increment(ref _calls);

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

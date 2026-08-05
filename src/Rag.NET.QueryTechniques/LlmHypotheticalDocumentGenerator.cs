using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;
using System.Runtime.ExceptionServices;

namespace Rag.NET.HyDE;

internal sealed partial class LlmHypotheticalDocumentGenerator(
    IChatClient chatClient,
    HydeOptions options,
    ILogger<LlmHypotheticalDocumentGenerator>? logger = null) : IHypotheticalDocumentGenerator
{
    /// <summary>Upper bound on concurrent LLM calls in <see cref="GenerateManyAsync"/>.</summary>
    private const int MaxConcurrency = 4;

    public async Task<string> GenerateAsync(string query, CancellationToken cancellationToken = default)
    {
        var docs = await GenerateManyAsync(query, count: 1, cancellationToken).ConfigureAwait(false);
        // Empty responses are dropped by GenerateManyAsync; preserve the historical
        // single-doc contract of returning an empty string in that case.
        return docs.Count > 0 ? docs[0] : string.Empty;
    }

    public async Task<IReadOnlyList<string>> GenerateManyAsync(string query, int count, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var prompt = options.PromptTemplate
            .Replace("{query}", query, StringComparison.Ordinal);
        var chatOptions = new ChatOptions { Temperature = options.HypothesisTemperature };

        using var throttle = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        var tasks = Enumerable.Range(0, count)
            .Select(_ => GenerateOneAsync(prompt, chatOptions, throttle, cancellationToken));
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

        var hypotheses = new List<string>(count);
        Exception? lastFailure = null;
        foreach (var (text, failure) in outcomes)
        {
            if (failure is not null)
            {
                lastFailure = failure;
                if (logger is not null)
                    LogHypothesisCallFailed(logger, failure);
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                hypotheses.Add(text);
            }
        }

        if (hypotheses.Count == 0 && lastFailure is not null)
            ExceptionDispatchInfo.Capture(lastFailure).Throw();

        return hypotheses;
    }

    /// <summary>
    /// One throttled LLM call. Non-cancellation failures are returned, not thrown, so a single
    /// bad call cannot fault the whole batch; cancellation always propagates.
    /// </summary>
    private async Task<(string? Text, Exception? Failure)> GenerateOneAsync(
        string prompt, ChatOptions chatOptions, SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
            return (response.Text, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (null, ex);
        }
        finally
        {
            throttle.Release();
        }
    }

    [LoggerMessage(EventId = 1516811390, EventName = "log_hypothesis_call_failed", Level = LogLevel.Debug, Message = "HyDE hypothesis generation call failed; discarding it and continuing with the surviving hypotheses")]
    private static partial void LogHypothesisCallFailed(ILogger logger, Exception exception);
}

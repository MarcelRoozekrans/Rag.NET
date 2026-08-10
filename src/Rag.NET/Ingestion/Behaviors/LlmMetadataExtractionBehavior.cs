using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;

namespace Rag.NET.Ingestion.Behaviors;

[Singleton]
public sealed class LlmMetadataExtractionBehavior : IIngestionBehavior
{
    [Inject(Required = false)] public IChatClient? ChatClient { get; set; }
    [Inject(Required = false)] public LlmMetadataExtractionOptions? ExtractionOptions { get; set; }
    [Inject(Required = false)] public ILogger<LlmMetadataExtractionBehavior>? Logger { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (ChatClient is null || ExtractionOptions is null)
            return await next(ctx, ct).ConfigureAwait(false);

        for (var i = 0; i < ctx.Chunks.Count; i++)
        {
            var chunkText = ctx.Chunks[i].Text;
            var chunkIndex = ctx.Chunks[i].ChunkIndex;
            var result = await ExtractAsync(chunkText, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                var tags = result.Value;
                foreach (var (key, value) in tags)
                {
                    if (IsKeyAllowed(key))
                        ctx.Chunks[i].Metadata.TryAdd(key, value);
                }
                RagPipelineLog.MetadataExtractionCompleted((ILogger?)Logger ?? NullLogger.Instance, tags.Count, chunkIndex);
            }
            else
            {
                RagPipelineLog.MetadataExtractionFailed((ILogger?)Logger ?? NullLogger.Instance, chunkIndex, result.Error);
            }
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }

    private bool IsKeyAllowed(string key)
    {
        if (ExtractionOptions!.Schema is null)
            return true;

        foreach (var attribute in ExtractionOptions.Schema)
        {
            if (string.Equals(attribute.Name, key, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private async ValueTask<Result<IReadOnlyDictionary<string, string>>> ExtractAsync(string text, CancellationToken ct)
    {
        try
        {
            var prompt = BuildPrompt(text);
            ChatMessage[] messages = [new(ChatRole.User, prompt)];
            var response = await ChatClient!.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);

            // Fenced or preambled replies used to land here whole and fail every chunk's
            // extraction; the per-chunk warning was the only trace.
            var json = LlmJsonExtractor.Extract(response.Text ?? "{}", LlmJsonPayloadKind.Object);

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null)
                return Result<IReadOnlyDictionary<string, string>>.Failure("LLM returned null JSON");

            return Result<IReadOnlyDictionary<string, string>>.Success(parsed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure(ex.ToString());
        }
    }

    private string BuildPrompt(string text)
    {
        if (ExtractionOptions!.Schema is { Count: > 0 } schema)
        {
            var fields = string.Join(", ", schema.Select(a => $"{a.Name} ({a.Description})"));
            return $$"""
                Extract metadata from the following text.
                Return a JSON object using only these fields: {{fields}}.
                Omit fields not present in the text. Return {} if nothing applies.
                Values must be strings.

                Text:
                {{text}}
                """;
        }

        return $$"""
            Extract metadata from the following text as a flat JSON object.
            Keys must be lowercase snake_case strings. Values must be strings.
            Return {} if nothing useful can be extracted.

            Text:
            {{text}}
            """;
    }
}

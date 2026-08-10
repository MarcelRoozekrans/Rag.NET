using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class ResumeChunkingStrategyTests
{
    private const string ValidJson = """
        {
          "contact_info": "John Smith, john@example.com",
          "work_history": [
            {"company": "Tech Corp", "title": "Engineer", "dates": "2020-2023", "description": "Led platform."}
          ],
          "education": [
            {"institution": "State University", "degree": "B.S. CS", "dates": "2016-2020"}
          ],
          "skills": "C#, Python, JavaScript"
        }
        """;

    private static IChatClient MakeChatClient(string response)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
        return client;
    }

    private static IEnumerable<DocumentSection> ResumeDoc(string text = "John Smith\njohn@example.com") =>
    [
        new() { Text = text, DocumentId = new DocumentId("resume"), SectionIndex = 0 }
    ];

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChunkDocumentAsync_CallsLlmOnce()
    {
        var client = MakeChatClient(ValidJson);
        var sut = new ResumeChunkingStrategy(client, new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ToAsync(ResumeDoc()), new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        await client.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChunkDocumentAsync_ProducesChunkPerWorkHistoryEntry()
    {
        var sut = new ResumeChunkingStrategy(MakeChatClient(ValidJson), new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ToAsync(ResumeDoc()), new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.Contains(chunks, c => c.Metadata.TryGetValue("section", out var s) && s == "work_history");
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsTemplateMetadata()
    {
        var sut = new ResumeChunkingStrategy(MakeChatClient(ValidJson), new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ToAsync(ResumeDoc()), new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.All(chunks, c => Assert.Equal<MetadataValue>("resume", c.Metadata["template"]));
    }

    public static TheoryData<string, string> WrappedResponseShapes() => new()
    {
        { "preamble then unlabelled fence", $"Here is the structured résumé in JSON format:\n\n```\n{ValidJson}\n```" },
        { "preamble then labelled fence", $"Sure! Here you go:\n\n```json\n{ValidJson}\n```" },
        { "preamble then bare json", $"Here is the JSON:\n\n{ValidJson}" },
        { "labelled fence only", $"```json\n{ValidJson}\n```" },
        { "fence then trailing prose", $"```\n{ValidJson}\n```\n\nLet me know if you need anything else." },
    };

    [Theory]
    [MemberData(nameof(WrappedResponseShapes))]
    public async Task ChunkDocumentAsync_WhenLlmWrapsTheJson_SectionsStillEmerge(string shape, string response)
    {
        // This site had no fence handling at all: any of these shapes threw in JsonNode.Parse
        // and silently downgraded the whole résumé to a single full-text chunk.
        var sut = new ResumeChunkingStrategy(MakeChatClient(response), new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ToAsync(ResumeDoc()), new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.True(
            chunks.Exists(c => c.Metadata.TryGetValue("section", out var s) && s == "work_history"),
            $"The '{shape}' response produced no work_history chunk. The JSON inside it is " +
            "valid, so parsing failed to find it and the résumé silently fell back to full text.");
        Assert.Contains(chunks, c => c.Metadata.TryGetValue("section", out var s) && s == "contact_info");
    }

    [Fact]
    public async Task ChunkDocumentAsync_FallsBackOnMalformedJson()
    {
        var sut = new ResumeChunkingStrategy(MakeChatClient("not valid json {{ "), new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ToAsync(ResumeDoc("Full resume text.")), new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        var fallback = Assert.Single(chunks);
        Assert.Contains("Full resume text.", fallback.Text, StringComparison.Ordinal);
    }
}

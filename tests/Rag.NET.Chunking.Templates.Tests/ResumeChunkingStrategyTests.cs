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

        Assert.Contains(chunks, c => c.Metadata.TryGetValue("section", out var s) && string.Equals(s, "work_history", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChunkDocumentAsync_AddsTemplateMetadata()
    {
        var sut = new ResumeChunkingStrategy(MakeChatClient(ValidJson), new ResumeChunkingOptions());

        var chunks = new List<TextChunk>();
        await foreach (var c in sut.ChunkDocumentAsync(ToAsync(ResumeDoc()), new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(c);

        Assert.All(chunks, c => Assert.Equal("resume", c.Metadata["template"]));
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

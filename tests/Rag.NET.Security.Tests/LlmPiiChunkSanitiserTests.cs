using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Models;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class LlmPiiChunkSanitiserTests
{
    private static readonly Dictionary<string, MetadataValue> Meta =
        new(StringComparer.Ordinal) { ["file_name"] = "test.txt" };

    private static IChatClient ClientReturning(string text)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))));
        return client;
    }

    [Fact]
    public void Sanitise_LlmReturnsRedactedText_UsesLlmOutput()
    {
        const string redacted = "Contact [EMAIL] for help.";
        var sut = new LlmPiiChunkSanitiser(ClientReturning(redacted), NullLogger<LlmPiiChunkSanitiser>.Instance);
        var result = sut.Sanitise("Contact alice@example.com for help.", Meta);
        Assert.Equal(redacted, result);
    }

    [Fact]
    public void Sanitise_LlmThrows_FallsBackToRegex()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new HttpRequestException("network error"));
        var sut = new LlmPiiChunkSanitiser(client, NullLogger<LlmPiiChunkSanitiser>.Instance);
        // Regex fallback should redact the email
        var result = sut.Sanitise("Email alice@example.com here.", Meta);
        Assert.Contains("[EMAIL]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_NullText_ReturnsEmpty()
    {
        var sut = new LlmPiiChunkSanitiser(ClientReturning(""), NullLogger<LlmPiiChunkSanitiser>.Instance);
        Assert.Equal(string.Empty, sut.Sanitise(null!, Meta));
    }

    [Fact]
    public void Sanitise_LlmThrowsOce_Propagates()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new OperationCanceledException());
        var sut = new LlmPiiChunkSanitiser(client, NullLogger<LlmPiiChunkSanitiser>.Instance);
        Assert.Throws<OperationCanceledException>(() => sut.Sanitise("text", Meta));
    }
}

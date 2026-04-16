using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class AuditAnswerEngineDecoratorTests
{
    private static readonly IReadOnlyList<SearchResult> Sources =
        [new SearchResult { Score = 1.0, Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d"), ChunkIndex = 0 } }];

    private static IAnswerEngine EngineReturning(string answer)
    {
        var engine = Substitute.For<IAnswerEngine>();
        engine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<Models.Options.RagOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(new RagResponse { Answer = answer, Sources = Sources }));
        return engine;
    }

    [Fact]
    public async Task AskAsync_LogsAnswerEvent_WithRequestId()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var ctx = new AuditCorrelationContext { RequestId = "req-123" };
        var opts = new AuditLogOptions { LogAnswerText = true };
        var sut = new AuditAnswerEngineDecorator(
            EngineReturning("The answer."), auditLog, ctx, opts,
            NullLogger<AuditAnswerEngineDecorator>.Instance);

        await sut.AskAsync("q", Sources, null, TestContext.Current.CancellationToken);

        await auditLog.Received(1).LogAnswerAsync(
            Arg.Is<AuditAnswerEvent>(e =>
                string.Equals(e.RequestId, "req-123", StringComparison.Ordinal) &&
                string.Equals(e.Answer, "The answer.", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_LogAnswerTextFalse_AnswerIsNull()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var ctx = new AuditCorrelationContext { RequestId = "req-456" };
        var opts = new AuditLogOptions { LogAnswerText = false };
        var sut = new AuditAnswerEngineDecorator(
            EngineReturning("secret"), auditLog, ctx, opts,
            NullLogger<AuditAnswerEngineDecorator>.Instance);

        await sut.AskAsync("q", Sources, null, TestContext.Current.CancellationToken);

        await auditLog.Received(1).LogAnswerAsync(
            Arg.Is<AuditAnswerEvent>(e => e.Answer == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_AuditLogThrows_AnswerStillReturned()
    {
        var auditLog = Substitute.For<IAuditLog>();
#pragma warning disable EPS06
        auditLog.LogAnswerAsync(Arg.Any<AuditAnswerEvent>(), Arg.Any<CancellationToken>())
                .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("db error"))));
#pragma warning restore EPS06
        var sut = new AuditAnswerEngineDecorator(
            EngineReturning("the answer"), auditLog,
            new AuditCorrelationContext(), new AuditLogOptions(),
            NullLogger<AuditAnswerEngineDecorator>.Instance);

        var result = await sut.AskAsync("q", Sources, null, TestContext.Current.CancellationToken);

        Assert.Equal("the answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_NoCorrelationRequestId_GeneratesOwnGuid()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var sut = new AuditAnswerEngineDecorator(
            EngineReturning("answer"), auditLog,
            new AuditCorrelationContext(), new AuditLogOptions(),
            NullLogger<AuditAnswerEngineDecorator>.Instance);

        await sut.AskAsync("q", Sources, null, TestContext.Current.CancellationToken);

        await auditLog.Received(1).LogAnswerAsync(
            Arg.Is<AuditAnswerEvent>(e => !string.IsNullOrEmpty(e.RequestId)),
            Arg.Any<CancellationToken>());
    }
}

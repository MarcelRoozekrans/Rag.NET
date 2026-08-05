using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class AuditRetrievalBehaviorTests
{
    private static RetrievalContext MakeCtx(string query = "test query") =>
        new() { Query = query, Options = new RetrievalOptions(), Logger = NullLogger.Instance };

    private static SearchResult MakeResult(string docId) =>
        new() { Score = 0.9, Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId(docId), ChunkIndex = 0 } };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    [Fact]
    public async Task HandleAsync_LogsRetrievalEvent_WithChunkRefs()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns(["user"]);
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, new AuditLogOptions(), NullLogger<AuditRetrievalBehavior>.Instance);

        var results = new[] { MakeResult("doc-1"), MakeResult("doc-2") };
        await sut.HandleAsync(MakeCtx("my query"), TestContext.Current.CancellationToken, NextReturning(results));

        await auditLog.Received(1).LogRetrievalAsync(
            Arg.Is<AuditRetrievalEvent>(e =>
                e!.Chunks.Count == 2 &&
                string.Equals(e.CallerRoles[0], "user", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LogQueryTextFalse_QueryIsNull()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns([]);
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, new AuditLogOptions { LogQueryText = false }, NullLogger<AuditRetrievalBehavior>.Instance);

        await sut.HandleAsync(MakeCtx("secret"), TestContext.Current.CancellationToken, NextReturning([]));

        await auditLog.Received(1).LogRetrievalAsync(
            Arg.Is<AuditRetrievalEvent>(e => e!.Query == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LogQueryTextTrue_QueryPopulated()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns([]);
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, new AuditLogOptions { LogQueryText = true }, NullLogger<AuditRetrievalBehavior>.Instance);

        await sut.HandleAsync(MakeCtx("find me"), TestContext.Current.CancellationToken, NextReturning([]));

        await auditLog.Received(1).LogRetrievalAsync(
            Arg.Is<AuditRetrievalEvent>(e => string.Equals(e!.Query, "find me", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_SetsRequestIdInExtensions()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns([]);
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, new AuditLogOptions(), NullLogger<AuditRetrievalBehavior>.Instance);
        var ctx = MakeCtx();

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning([]));

        Assert.True(ctx.Extensions.ContainsKey("audit_request_id"));
    }

    [Fact]
    public async Task HandleAsync_AuditLogThrows_ResultsStillReturned()
    {
        var auditLog = Substitute.For<IAuditLog>();
#pragma warning disable EPS06 // ValueTask struct copy — intentional test double setup via NSubstitute
        auditLog.LogRetrievalAsync(Arg.Any<AuditRetrievalEvent>(), Arg.Any<CancellationToken>())
                .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("db error"))));
#pragma warning restore EPS06
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns([]);
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, new AuditLogOptions(), NullLogger<AuditRetrievalBehavior>.Instance);
        var results = new[] { MakeResult("doc-1") };

        var returned = await sut.HandleAsync(MakeCtx(), TestContext.Current.CancellationToken, NextReturning(results));

        Assert.Single(returned);
    }
}

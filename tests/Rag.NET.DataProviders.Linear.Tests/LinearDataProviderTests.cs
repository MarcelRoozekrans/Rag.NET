using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Linear;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Linear.Tests;

public sealed class LinearDataProviderTests
{
    private static LinearDataProvider MakeProvider(
        ILinearApi api,
        LinearOptions? options = null,
        Microsoft.Extensions.Logging.ILogger<LinearDataProvider>? logger = null)
        => new(api, options ?? new LinearOptions(), logger);

    private static async Task<string> ReadContentAsync(FileEntry entry)
    {
        await using var stream = await entry.OpenContentAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static LinearGraphQlResponse Page(bool hasNext, string? endCursor, params LinearIssue[] issues)
        => new()
        {
            Data = new LinearIssuesData
            {
                Issues = new LinearIssueConnection
                {
                    Nodes    = issues,
                    PageInfo = new LinearPageInfo { HasNextPage = hasNext, EndCursor = endCursor },
                },
            },
        };

    private static LinearIssue Issue(
        string identifier = "ENG-1",
        string title = "Fix login bug",
        string? description = "Steps to reproduce the login failure.",
        DateTimeOffset? updatedAt = null,
        LinearWorkflowState? state = null,
        LinearProject? project = null,
        LinearUser? assignee = null,
        LinearTeam? team = null,
        LinearCommentConnection? comments = null)
        => new()
        {
            Identifier  = identifier,
            Title       = title,
            Description = description,
            UpdatedAt   = updatedAt ?? new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
            Url         = $"https://linear.app/acme/issue/{identifier}",
            State       = state,
            Project     = project,
            Assignee    = assignee,
            Team        = team,
            Comments    = comments,
        };

    // 1. Single page: exact Markdown rendering + handle mapping.
    [Fact]
    public async Task GetFilesAsync_SinglePage_RendersMarkdownAndMapsHandle()
    {
        var issue = Issue(
            state:    new LinearWorkflowState { Name = "In Progress", Type = "started" },
            project:  new LinearProject { Name = "Auth" },
            assignee: new LinearUser { Name = "Alice" },
            team:     new LinearTeam { Key = "ENG" },
            comments: new LinearCommentConnection
            {
                Nodes =
                [
                    new LinearComment
                    {
                        Body      = "Looks good",
                        CreatedAt = new DateTimeOffset(2026, 7, 1, 10, 30, 0, TimeSpan.Zero),
                        User      = new LinearUser { Name = "Bob" },
                    },
                ],
            });
        var api = new FakeLinearApi(Page(hasNext: false, endCursor: null, issue));
        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(results).Value;
        Assert.Equal("ENG-1", entry.Id.Value);
        Assert.Equal("ENG-1 Fix login bug.md", entry.FileName);
        Assert.Equal("2026-07-01T12:00:00.0000000+00:00", entry.ETag);
        Assert.Equal("ENG", entry.Metadata!["team"]);
        Assert.Equal("In Progress", entry.Metadata["state"]);
        Assert.Equal("Auth", entry.Metadata["project"]);
        Assert.Equal("https://linear.app/acme/issue/ENG-1", entry.Metadata["url"]);

        var content = await ReadContentAsync(entry);
        Assert.Equal(
            """
            # ENG-1: Fix login bug

            **State:** In Progress  **Project:** Auth  **Assignee:** Alice

            Steps to reproduce the login failure.

            ## Comments
            **Bob** (2026-07-01 10:30): Looks good
            """.ReplaceLineEndings("\n"),
            content.ReplaceLineEndings("\n"));
    }

    // 2. Cursor pagination: two pages, captured after-cursors.
    [Fact]
    public async Task GetFilesAsync_CursorPagination_FollowsEndCursor()
    {
        var api = new FakeLinearApi(
            Page(hasNext: true, endCursor: "cursor-1", Issue(identifier: "ENG-1")),
            Page(hasNext: false, endCursor: null, Issue(identifier: "ENG-2")));
        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("ENG-1", results[0].Value.Id.Value);
        Assert.Equal("ENG-2", results[1].Value.Id.Value);

        Assert.Equal(2, api.Requests.Count);
        Assert.Null(api.Requests[0].Variables.After);
        Assert.Equal("cursor-1", api.Requests[1].Variables.After);
        Assert.All(api.Requests, r => Assert.Equal(LinearDataProvider.IssuesQuery, r.Query));
    }

    // 3. Team/state filters land in the request variables.
    [Fact]
    public async Task GetFilesAsync_TeamAndStateFilters_SentAsVariables()
    {
        var api  = new FakeLinearApi(Page(hasNext: false, endCursor: null));
        var opts = new LinearOptions
        {
            TeamKeys = ["ENG", "OPS"],
            States   = ["started", "completed"],
            PageSize = 25,
        };
        var sut = MakeProvider(api, opts);

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var variables = Assert.Single(api.Requests).Variables;
        Assert.Equal(25, variables.First);
        Assert.NotNull(variables.Filter);
        Assert.Equal(["ENG", "OPS"], variables.Filter!.Team!.Key.In);
        Assert.Equal(["started", "completed"], variables.Filter.State!.Type.In);
        Assert.Null(variables.Filter.UpdatedAt);
    }

    // 4. DeltaToken applied as updatedAt gt filter + watermark advances after a full traversal.
    [Fact]
    public async Task GetFilesAsync_DeltaToken_AppliedAsUpdatedAtFilter_AndWatermarkAdvances()
    {
        var api = new FakeLinearApi(Page(
            hasNext: false, endCursor: null,
            Issue(identifier: "ENG-1", updatedAt: new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero)),
            Issue(identifier: "ENG-2", updatedAt: new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero))));
        var opts = new LinearOptions { DeltaToken = "2026-07-01T00:00:00.0000000+00:00" };
        var sut  = MakeProvider(api, opts);

        Assert.Null(sut.GetDeltaToken()); // nothing enumerated yet

        await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var filter = Assert.Single(api.Requests).Variables.Filter;
        Assert.NotNull(filter);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), filter!.UpdatedAt!.Gt);

        Assert.Equal("2026-07-03T09:00:00.0000000+00:00", sut.GetDeltaToken());
    }

    // 4b. Invalid DeltaToken → ValidationFailed failure naming the token.
    [Fact]
    public async Task GetFilesAsync_InvalidDeltaToken_YieldsValidationFailure()
    {
        var api  = new FakeLinearApi(Page(hasNext: false, endCursor: null));
        var opts = new LinearOptions { DeltaToken = "not-a-date" };
        var sut  = MakeProvider(api, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var single = Assert.Single(results);
        Assert.True(single.IsFailure);
        var failure = Assert.IsType<RagError.ValidationFailed>(single.Error);
        Assert.Contains("not-a-date", failure.Failures[0].ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(api.Requests);
        Assert.Null(sut.GetDeltaToken());
    }

    // 5. GraphQL errors array → failure naming the messages.
    [Fact]
    public async Task GetFilesAsync_GraphQlErrors_YieldFailureNamingMessages()
    {
        var api = new FakeLinearApi(new LinearGraphQlResponse
        {
            Errors =
            [
                new LinearGraphQlError { Message = "Entity not found" },
                new LinearGraphQlError { Message = "Rate limited" },
            ],
        });
        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var single = Assert.Single(results);
        Assert.True(single.IsFailure);
        var failure = Assert.IsType<RagError.HttpFailed>(single.Error);
        Assert.Contains("Entity not found", failure.Content, StringComparison.Ordinal);
        Assert.Contains("Rate limited", failure.Content, StringComparison.Ordinal);
    }

    // 6. HTTP failure propagates as RagError.HttpFailed.
    [Fact]
    public async Task GetFilesAsync_HttpFailure_PropagatesAsRagErrorHttpFailed()
    {
        var api = new FakeLinearApiHttpError(HttpStatusCode.Unauthorized, "Unauthorized");
        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var single = Assert.Single(results);
        Assert.True(single.IsFailure);
        var failure = Assert.IsType<RagError.HttpFailed>(single.Error);
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
    }

    // 6b. A failure mid-pagination must not advance the watermark past unseen issues.
    [Fact]
    public async Task GetFilesAsync_FailureMidPagination_DoesNotAdvanceWatermark()
    {
        var api = new FakeLinearApiFailsOnSecondPage(
            Page(hasNext: true, endCursor: "cursor-1",
                Issue(identifier: "ENG-1", updatedAt: new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero))));
        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsSuccess);
        Assert.True(results[1].IsFailure);

        // Sort direction of orderBy is undocumented — an incomplete traversal returns null
        // (keep the previous token) rather than the max updatedAt seen so far.
        Assert.Null(sut.GetDeltaToken());
    }

    // 6c. Malformed page (hasNextPage true, no endCursor): stop, withhold watermark, warn.
    [Fact]
    public async Task GetFilesAsync_HasNextPageWithoutEndCursor_StopsAndWithholdsWatermark()
    {
        var api = new FakeLinearApi(Page(
            hasNext: true, endCursor: null,
            Issue(identifier: "ENG-1", updatedAt: new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero))));
        var logger = new CapturingLogger<LinearDataProvider>();
        var sut    = MakeProvider(api, logger: logger);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The loop exits after the malformed page instead of re-requesting forever.
        var single = Assert.Single(results);
        Assert.True(single.IsSuccess);
        _ = Assert.Single(api.Requests);

        // The traversal is incomplete — the watermark must not advance past unseen issues.
        Assert.Null(sut.GetDeltaToken());

        var warning = Assert.Single(logger.Entries,
            e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("hasNextPage", warning.Message, StringComparison.Ordinal);
    }

    // 7. Invalid state value throws at registration.
    [Fact]
    public void AddLinearDataProvider_InvalidStateValue_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() =>
            services.AddLinearDataProvider(
                apiKey: "lin_api_test",
                configure: opts => opts.States = ["cancelled"])); // British spelling — invalid

        Assert.Contains("cancelled", ex.Message, StringComparison.Ordinal);
        Assert.Contains("canceled", ex.Message, StringComparison.Ordinal);
    }

    // 8. DI registration resolves the provider; empty apiKey throws.
    [Fact]
    public void AddLinearDataProvider_ResolvesProvider()
    {
        var services = new ServiceCollection();
        services.AddLinearDataProvider(
            apiKey: "lin_api_test",
            configure: opts => opts.States = ["started", "canceled"]);

        var provider = services.BuildServiceProvider().GetRequiredService<IFileContentProvider>();

        _ = Assert.IsType<LinearDataProvider>(provider);
    }

    [Fact]
    public void AddLinearDataProvider_EmptyApiKey_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddLinearDataProvider(apiKey: " "));
    }

    // 9. Issue without comments/description/state: sections omitted gracefully.
    [Fact]
    public async Task GetFilesAsync_NoOptionalFields_OmitsSections()
    {
        var api = new FakeLinearApi(Page(
            hasNext: false, endCursor: null,
            Issue(identifier: "ENG-9", title: "Bare issue", description: null)));
        var sut = MakeProvider(api);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry   = Assert.Single(results).Value;
        var content = await ReadContentAsync(entry);

        Assert.Equal("# ENG-9: Bare issue", content);
        Assert.DoesNotContain("## Comments", content, StringComparison.Ordinal);
        Assert.DoesNotContain("**State:**", content, StringComparison.Ordinal);
        // Optional metadata entries are absent; url is always present.
        Assert.False(entry.Metadata!.ContainsKey("team"));
        Assert.False(entry.Metadata.ContainsKey("project"));
        Assert.True(entry.Metadata.ContainsKey("url"));
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LinearDataProvider(null!, new LinearOptions()));
    }
}

// ---------------------------------------------------------------------------
// Fake ILinearApi implementations
// ---------------------------------------------------------------------------

file sealed class FakeLinearApi(params LinearGraphQlResponse[] responses) : ILinearApi
{
    private int _call;

    public List<LinearGraphQlRequest> Requests { get; } = [];

    public Task<Result<LinearGraphQlResponse, HttpError>> QueryAsync(
        LinearGraphQlRequest body,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(body);
        var response = responses[Math.Min(_call, responses.Length - 1)];
        _call++;
        return Task.FromResult(Result<LinearGraphQlResponse, HttpError>.Success(response));
    }
}

file sealed class FakeLinearApiHttpError(HttpStatusCode statusCode, string message) : ILinearApi
{
    public Task<Result<LinearGraphQlResponse, HttpError>> QueryAsync(
        LinearGraphQlRequest body,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<LinearGraphQlResponse, HttpError>.Failure(
            new HttpError(
                statusCode,
                System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty,
                message)));
    }
}

file sealed class FakeLinearApiFailsOnSecondPage(LinearGraphQlResponse firstPage) : ILinearApi
{
    private int _call;

    public Task<Result<LinearGraphQlResponse, HttpError>> QueryAsync(
        LinearGraphQlRequest body,
        CancellationToken cancellationToken = default)
    {
        if (_call++ == 0)
            return Task.FromResult(Result<LinearGraphQlResponse, HttpError>.Success(firstPage));

        return Task.FromResult(Result<LinearGraphQlResponse, HttpError>.Failure(
            new HttpError(
                HttpStatusCode.InternalServerError,
                System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty,
                "boom")));
    }
}

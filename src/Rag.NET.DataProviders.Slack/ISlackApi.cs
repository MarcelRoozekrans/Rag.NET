using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Slack;

[ZeroAllocRestClient]
internal interface ISlackApi
{
    [Get("/api/conversations.list")]
    Task<Result<SlackChannelList, ZeroAlloc.Rest.HttpError>> ListChannelsAsync(
        [Query] int limit = 200,
        [Query] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/conversations.history")]
    Task<Result<SlackMessageList, ZeroAlloc.Rest.HttpError>> GetHistoryAsync(
        [Query] string channel,
        [Query] int limit = 200,
        [Query] string? oldest = null,
        [Query] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/conversations.replies")]
    Task<Result<SlackMessageList, ZeroAlloc.Rest.HttpError>> GetRepliesAsync(
        [Query] string channel,
        [Query] string ts,
        CancellationToken cancellationToken = default);

    [Get("/api/users.info")]
    Task<Result<SlackUserInfo, ZeroAlloc.Rest.HttpError>> GetUserAsync(
        [Query] string user,
        CancellationToken cancellationToken = default);
}

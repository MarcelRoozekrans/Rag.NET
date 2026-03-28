using Refit;

namespace Rag.NET.DataProviders.Slack;

[Headers("Accept: application/json")]
internal interface ISlackApi
{
    [Get("/api/conversations.list")]
    Task<SlackChannelList> ListChannelsAsync(
        [Query] int limit = 200,
        [Query] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/conversations.history")]
    Task<SlackMessageList> GetHistoryAsync(
        [Query] string channel,
        [Query] int limit = 200,
        [Query] string? oldest = null,
        [Query] string? cursor = null,
        CancellationToken cancellationToken = default);

    [Get("/api/conversations.replies")]
    Task<SlackMessageList> GetRepliesAsync(
        [Query] string channel,
        [Query] string ts,
        CancellationToken cancellationToken = default);

    [Get("/api/users.info")]
    Task<SlackUserInfo> GetUserAsync(
        [Query] string user,
        CancellationToken cancellationToken = default);
}

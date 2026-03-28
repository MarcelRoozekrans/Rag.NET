# Collaboration & Communication Connectors — Implementation Plan (Part 2)

Tasks 5–8. Continues from `2026-03-28-collaboration-communication-connectors-implementation-part1.md`.

---

## Task 5: Slack connector

**Files:**
- Create: `src/Rag.NET.DataProviders.Slack/Rag.NET.DataProviders.Slack.csproj`
- Create: `src/Rag.NET.DataProviders.Slack/SlackOptions.cs`
- Create: `src/Rag.NET.DataProviders.Slack/SlackApi.cs`
- Create: `src/Rag.NET.DataProviders.Slack/SlackDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Slack/SlackDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Slack.Tests/Rag.NET.DataProviders.Slack.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Slack.Tests/SlackDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

### csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Slack</RootNamespace>
    <PackageId>Rag.NET.DataProviders.Slack</PackageId>
    <Description>Slack data provider for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Refit" Version="8.*" />
    <PackageReference Include="Refit.HttpClientFactory" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.DataProviders.Slack.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

### SlackOptions.cs

```csharp
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Slack;

public sealed class SlackOptions : CloudStorageOptions
{
    public string? ChannelId    { get; init; }   // null = all joined channels
    public int     MessageLimit { get; init; } = 200;
}
```

### SlackApi.cs

```csharp
using System.Text.Json.Serialization;
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

internal sealed record SlackChannelList(
    [property: JsonPropertyName("ok")]                bool Ok,
    [property: JsonPropertyName("channels")]          List<SlackChannel> Channels,
    [property: JsonPropertyName("response_metadata")] SlackCursor? ResponseMetadata);

internal sealed record SlackChannel(
    [property: JsonPropertyName("id")]   string Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record SlackMessageList(
    [property: JsonPropertyName("ok")]                bool Ok,
    [property: JsonPropertyName("messages")]          List<SlackMessage> Messages,
    [property: JsonPropertyName("response_metadata")] SlackCursor? ResponseMetadata);

internal sealed record SlackMessage(
    [property: JsonPropertyName("ts")]          string Ts,
    [property: JsonPropertyName("user")]        string? User,
    [property: JsonPropertyName("text")]        string Text,
    [property: JsonPropertyName("thread_ts")]   string? ThreadTs,
    [property: JsonPropertyName("reply_count")] int? ReplyCount);

internal sealed record SlackCursor(
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

internal sealed record SlackUserInfo(
    [property: JsonPropertyName("ok")]   bool Ok,
    [property: JsonPropertyName("user")] SlackUser? User);

internal sealed record SlackUser(
    [property: JsonPropertyName("real_name")] string RealName);
```

### SlackDataProvider.cs

Key design: messages are batched **per channel per day** into a single `FileHandle`. This keeps chunk count manageable and gives each document a stable, meaningful filename.

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Slack;

public sealed class SlackDataProvider : FileContentProviderBase
{
    private readonly ISlackApi    _api;
    private readonly SlackOptions _options;

    public SlackDataProvider(ISlackApi api, SlackOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api     = api;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetHandlesAsync(cancellationToken);

    private async IAsyncEnumerable<FileHandle> GetHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channels = await GetChannelsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messages = await FetchMessagesAsync(channel.Id, cancellationToken)
                .ConfigureAwait(false);

            // Group messages by UTC calendar day
            var byDay = messages.GroupBy(m =>
                DateTimeOffset
                    .FromUnixTimeMilliseconds(
                        (long)(double.Parse(m.Ts, System.Globalization.CultureInfo.InvariantCulture) * 1000))
                    .UtcDateTime.Date);

            foreach (var group in byDay)
            {
                var date     = group.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var lastTs   = group.OrderByDescending(m => m.Ts).First().Ts;
                var markdown = await BuildDayMarkdownAsync(
                    channel.Id, channel.Name, date, group.ToList(), cancellationToken)
                    .ConfigureAwait(false);

                yield return new FileHandle(
                    Id:               $"{channel.Id}/{date}",
                    FileName:         $"{channel.Name}-{date}.md",
                    ETag:             lastTs,
                    OpenContentAsync: _ => Task.FromResult<Stream>(
                        new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
            }
        }
    }

    private async Task<List<SlackChannel>> GetChannelsAsync(CancellationToken ct)
    {
        if (_options.ChannelId is not null)
            return [new SlackChannel(_options.ChannelId, _options.ChannelId)];

        var channels = new List<SlackChannel>();
        string? cursor = null;
        do
        {
            var result = await _api.ListChannelsAsync(cursor: cursor, cancellationToken: ct)
                .ConfigureAwait(false);
            channels.AddRange(result.Channels);
            cursor = string.IsNullOrEmpty(result.ResponseMetadata?.NextCursor)
                ? null : result.ResponseMetadata.NextCursor;
        }
        while (cursor is not null);
        return channels;
    }

    private async Task<List<SlackMessage>> FetchMessagesAsync(string channelId, CancellationToken ct)
    {
        var messages = new List<SlackMessage>();
        string? cursor = null;
        var oldest = _options.DeltaToken;   // unix timestamp string stored as DeltaToken

        do
        {
            var result = await _api.GetHistoryAsync(
                channelId, _options.MessageLimit, oldest, cursor, ct)
                .ConfigureAwait(false);
            messages.AddRange(result.Messages);
            cursor = string.IsNullOrEmpty(result.ResponseMetadata?.NextCursor)
                ? null : result.ResponseMetadata.NextCursor;
        }
        while (cursor is not null);

        return messages;
    }

    private async Task<string> BuildDayMarkdownAsync(
        string channelId, string channelName, string date,
        List<SlackMessage> messages, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# #{channelName} — {date}");
        sb.AppendLine();

        foreach (var msg in messages.OrderBy(m => m.Ts))
        {
            var time = DateTimeOffset
                .FromUnixTimeMilliseconds(
                    (long)(double.Parse(msg.Ts, System.Globalization.CultureInfo.InvariantCulture) * 1000))
                .ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

            var userName = msg.User is not null
                ? await GetUserNameAsync(msg.User, ct).ConfigureAwait(false)
                : "unknown";

            sb.AppendLine($"**{userName}** ({time}): {msg.Text}");

            if (msg.ReplyCount is > 0 && msg.ThreadTs is not null)
            {
                var replies = await _api.GetRepliesAsync(channelId, msg.ThreadTs, ct)
                    .ConfigureAwait(false);

                foreach (var reply in replies.Messages.Skip(1))   // skip parent message
                {
                    var replyTime = DateTimeOffset
                        .FromUnixTimeMilliseconds(
                            (long)(double.Parse(reply.Ts,
                                System.Globalization.CultureInfo.InvariantCulture) * 1000))
                        .ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                    sb.AppendLine($"> {replyTime}: {reply.Text}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    // Simple in-memory cache to avoid repeated API calls per batch
    private readonly Dictionary<string, string> _userCache = new(StringComparer.Ordinal);

    private async Task<string> GetUserNameAsync(string userId, CancellationToken ct)
    {
        if (_userCache.TryGetValue(userId, out var name)) return name;
        var info = await _api.GetUserAsync(userId, ct).ConfigureAwait(false);
        name = info.User?.RealName ?? userId;
        _userCache[userId] = name;
        return name;
    }
}
```

### SlackDataProviderExtensions.cs

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Refit;

namespace Rag.NET.DataProviders.Slack;

public static class SlackDataProviderExtensions
{
    public static IServiceCollection AddSlackDataProvider(
        this IServiceCollection services,
        string botToken,
        Action<SlackOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);

        var opts = new SlackOptions();
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("Slack").AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Slack");
            http.BaseAddress = new Uri("https://slack.com");
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", botToken);
            return new SlackDataProvider(RestService.For<ISlackApi>(http), opts);
        });
    }
}
```

### Tests csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Slack.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders.Slack\Rag.NET.DataProviders.Slack.csproj" />
  </ItemGroup>
  <!-- inherit shared test props (xUnit, coverlet, etc.) from Directory.Build.props -->
</Project>
```

### SlackDataProviderTests.cs

4 tests using a fake HTTP handler that stubs the Refit-generated calls:

**Test 1 — Full traversal, single-channel, same-day batch**

Stub:
- `GET /api/conversations.list` → `{ "ok": true, "channels": [{ "id": "C001", "name": "general" }] }`
- `GET /api/conversations.history?channel=C001` → two messages with the same UTC date timestamp

Assert:
- 1 `FileEntry` yielded (both messages collapsed into one day batch)
- `FileName` == `"general-{date}.md"` where `{date}` is the UTC date of the messages
- `ETag` equals the latest `ts` value of the two messages

**Test 2 — Delta run: `DeltaToken` is forwarded as `oldest` query parameter**

Set `DeltaToken = "1711929600.000000"` on `SlackOptions`. Stub `conversations.history` to capture the full request URL. Assert that the URL contains `oldest=1711929600.000000`.

**Test 3 — Extension filter excludes non-matching files**

Set `Extensions = [".txt"]` on `SlackOptions` (all generated files are `.md`). Assert that `GetFilesAsync` yields zero entries.

**Test 4 — Constructor null guard**

```csharp
Assert.Throws<ArgumentNullException>(() => new SlackDataProvider(null!, new SlackOptions()));
```

Infrastructure: `FakeSlackHandler` (same pattern as `FakeGraphHandler` in SharePoint tests) — matches requests by URL path substring and returns pre-configured JSON strings.

### `Rag.NET.slnx` additions

Inside `<Folder Name="/src/">`:
```xml
<Project Path="src/Rag.NET.DataProviders.Slack/Rag.NET.DataProviders.Slack.csproj" />
```

Inside `<Folder Name="/tests/">`:
```xml
<Project Path="tests/Rag.NET.DataProviders.Slack.Tests/Rag.NET.DataProviders.Slack.Tests.csproj" />
```

### Commit

```bash
git add src/Rag.NET.DataProviders.Slack/ tests/Rag.NET.DataProviders.Slack.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.Slack connector"
```

---

## Task 6: Microsoft Teams connector

**Files:**
- Create: `src/Rag.NET.DataProviders.MicrosoftTeams/Rag.NET.DataProviders.MicrosoftTeams.csproj`
- Create: `src/Rag.NET.DataProviders.MicrosoftTeams/MicrosoftTeamsOptions.cs`
- Create: `src/Rag.NET.DataProviders.MicrosoftTeams/MicrosoftTeamsDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.MicrosoftTeams/MicrosoftTeamsDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.MicrosoftTeams.Tests/Rag.NET.DataProviders.MicrosoftTeams.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.MicrosoftTeams.Tests/MicrosoftTeamsDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

### csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.MicrosoftTeams</RootNamespace>
    <PackageId>Rag.NET.DataProviders.MicrosoftTeams</PackageId>
    <Description>Microsoft Teams data provider for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="Microsoft.Graph" Version="5.*" />
    <PackageReference Include="Azure.Identity" Version="1.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.DataProviders.MicrosoftTeams.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

### MicrosoftTeamsOptions.cs

```csharp
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.MicrosoftTeams;

public sealed class MicrosoftTeamsOptions : CloudStorageOptions
{
    /// <summary>Pin to a specific team. <c>null</c> = all joined teams.</summary>
    public string? TeamId    { get; init; }
    /// <summary>Pin to a specific channel within <see cref="TeamId"/>. <c>null</c> = all channels.</summary>
    public string? ChannelId { get; init; }
}
```

### MicrosoftTeamsDataProvider.cs

Uses the Graph SDK (same dependency as `SharePointDataProvider`). Messages are batched **per channel per day** into a single `FileHandle`, matching the Slack connector's design. HTML tags are stripped from Teams message bodies using a simple regex.

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.MicrosoftTeams;

public sealed class MicrosoftTeamsDataProvider : FileContentProviderBase
{
    private readonly GraphServiceClient      _graph;
    private readonly MicrosoftTeamsOptions   _options;

    public MicrosoftTeamsDataProvider(GraphServiceClient graph, MicrosoftTeamsOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph   = graph;
        _options = options;
    }

    protected override IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        CancellationToken cancellationToken)
        => GetFullHandlesAsync(cancellationToken);
    // Note: Graph Teams messages delta endpoint exists but requires additional app permissions.
    // DeltaToken support can be layered on top in a follow-up task.

    private async IAsyncEnumerable<FileHandle> GetFullHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var teams = await GetTeamsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (teamId, _) in teams)
        {
            var channels = await GetChannelsAsync(teamId, cancellationToken).ConfigureAwait(false);

            foreach (var (channelId, channelName) in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var messages = await FetchMessagesAsync(teamId, channelId, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var handle in GroupByDay(teamId, channelId, channelName, messages))
                    yield return handle;
            }
        }
    }

    private async Task<List<(string Id, string Name)>> GetTeamsAsync(CancellationToken ct)
    {
        if (_options.TeamId is not null)
            return [(_options.TeamId, _options.TeamId)];

        var result = await _graph.Me.JoinedTeams.GetAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        return (result?.Value ?? [])
            .Select(t => (t.Id ?? string.Empty, t.DisplayName ?? t.Id ?? string.Empty))
            .Where(t => !string.IsNullOrEmpty(t.Item1))
            .ToList();
    }

    private async Task<List<(string Id, string Name)>> GetChannelsAsync(
        string teamId, CancellationToken ct)
    {
        if (_options.ChannelId is not null)
            return [(_options.ChannelId, _options.ChannelId)];

        var result = await _graph.Teams[teamId].Channels.GetAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        return (result?.Value ?? [])
            .Select(c => (c.Id ?? string.Empty, c.DisplayName ?? c.Id ?? string.Empty))
            .Where(c => !string.IsNullOrEmpty(c.Item1))
            .ToList();
    }

    private async Task<List<Microsoft.Graph.Models.ChatMessage>> FetchMessagesAsync(
        string teamId, string channelId, CancellationToken ct)
    {
        var all  = new List<Microsoft.Graph.Models.ChatMessage>();
        var page = await _graph.Teams[teamId].Channels[channelId].Messages
            .GetAsync(cancellationToken: ct).ConfigureAwait(false);

        while (page is not null)
        {
            all.AddRange(page.Value ?? []);
            page = page.OdataNextLink is not null
                ? await _graph.Teams[teamId].Channels[channelId].Messages
                    .WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: ct).ConfigureAwait(false)
                : null;
        }

        return all;
    }

    private static IEnumerable<FileHandle> GroupByDay(
        string teamId, string channelId, string channelName,
        List<Microsoft.Graph.Models.ChatMessage> messages)
    {
        var byDay = messages
            .Where(m => m.Body?.Content is not null)
            .GroupBy(m => m.CreatedDateTime?.UtcDateTime.Date ?? DateTime.UtcNow.Date);

        foreach (var group in byDay)
        {
            var date    = group.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var lastMod = group.Max(m => m.LastModifiedDateTime?.ToString("o",
                System.Globalization.CultureInfo.InvariantCulture));
            var markdown = BuildDayMarkdown(channelName, date, group.ToList());

            yield return new FileHandle(
                Id:               $"{teamId}/{channelId}/{date}",
                FileName:         $"{channelName}-{date}.md",
                ETag:             lastMod,
                OpenContentAsync: _ => Task.FromResult<Stream>(
                    new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
        }
    }

    private static string BuildDayMarkdown(
        string channelName, string date,
        List<Microsoft.Graph.Models.ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {channelName} — {date}");
        sb.AppendLine();

        foreach (var msg in messages.OrderBy(m => m.CreatedDateTime))
        {
            var time   = msg.CreatedDateTime?.ToString("HH:mm",
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var author = msg.From?.User?.DisplayName ?? "unknown";
            // Strip HTML tags emitted by the Teams rich-text editor
            var body   = System.Text.RegularExpressions.Regex
                .Replace(msg.Body?.Content ?? string.Empty, "<[^>]+>", string.Empty)
                .Trim();
            sb.AppendLine($"**{author}** ({time}): {body}");
        }

        return sb.ToString().TrimEnd();
    }
}
```

### MicrosoftTeamsDataProviderExtensions.cs

```csharp
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.MicrosoftTeams;

public static class MicrosoftTeamsDataProviderExtensions
{
    public static IServiceCollection AddMicrosoftTeamsDataProvider(
        this IServiceCollection services,
        string tenantId,
        string clientId,
        string clientSecret,
        Action<MicrosoftTeamsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        var opts = new MicrosoftTeamsOptions();
        configure?.Invoke(opts);

        services.AddDataProviderHttpClient("MicrosoftTeams").AddStandardResilienceHandler();

        return services.AddSingleton<IFileContentProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient("MicrosoftTeams");
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graph      = new GraphServiceClient(httpClient, credential);
            return new MicrosoftTeamsDataProvider(graph, opts);
        });
    }
}
```

### Tests csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.MicrosoftTeams.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders.MicrosoftTeams\Rag.NET.DataProviders.MicrosoftTeams.csproj" />
  </ItemGroup>
</Project>
```

### MicrosoftTeamsDataProviderTests.cs

Same fake HTTP handler pattern as `SharePointDataProviderTests` — a `FakeGraphHandler` keyed on URL path substrings.

Stub URL responses:

```
GET /v1.0/me/joinedTeams
→ { "value": [{ "id": "team-1", "displayName": "Engineering" }] }

GET /v1.0/teams/team-1/channels
→ { "value": [{ "id": "chan-1", "displayName": "general" }] }

GET /v1.0/teams/team-1/channels/chan-1/messages
→ {
    "value": [{
      "id": "msg-1",
      "createdDateTime": "2026-03-01T10:00:00Z",
      "lastModifiedDateTime": "2026-03-01T10:00:00Z",
      "from": { "user": { "displayName": "Alice" } },
      "body": { "content": "Hello team", "contentType": "text" }
    }]
  }
```

4 tests:

**Test 1 — Full traversal**

Assert: 1 `FileEntry`, `FileName == "general-2026-03-01.md"`.

**Test 2 — TeamId + ChannelId pin skips the list endpoints**

Set `TeamId = "team-1"` and `ChannelId = "chan-1"`. Provide only the messages stub (no `joinedTeams` or `channels` stubs). Assert: 1 entry returned and no HTTP 404 errors propagated.

**Test 3 — Extension filter**

Set `Extensions = [".txt"]`. Assert: 0 entries (generated files are `.md`).

**Test 4 — Constructor null guard**

```csharp
Assert.Throws<ArgumentNullException>(() =>
    new MicrosoftTeamsDataProvider(null!, new MicrosoftTeamsOptions()));
```

### `Rag.NET.slnx` additions

Inside `<Folder Name="/src/">`:
```xml
<Project Path="src/Rag.NET.DataProviders.MicrosoftTeams/Rag.NET.DataProviders.MicrosoftTeams.csproj" />
```

Inside `<Folder Name="/tests/">`:
```xml
<Project Path="tests/Rag.NET.DataProviders.MicrosoftTeams.Tests/Rag.NET.DataProviders.MicrosoftTeams.Tests.csproj" />
```

### Commit

```bash
git add src/Rag.NET.DataProviders.MicrosoftTeams/ tests/Rag.NET.DataProviders.MicrosoftTeams.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.MicrosoftTeams connector"
```

---

## Task 7: Gmail connector

**Files:**
- Create: `src/Rag.NET.DataProviders.Gmail/Rag.NET.DataProviders.Gmail.csproj`
- Create: `src/Rag.NET.DataProviders.Gmail/GmailOptions.cs`
- Create: `src/Rag.NET.DataProviders.Gmail/GmailDataProvider.cs`
- Create: `src/Rag.NET.DataProviders.Gmail/GmailDataProviderExtensions.cs`
- Create: `tests/Rag.NET.DataProviders.Gmail.Tests/Rag.NET.DataProviders.Gmail.Tests.csproj`
- Create: `tests/Rag.NET.DataProviders.Gmail.Tests/GmailDataProviderTests.cs`
- Modify: `Rag.NET.slnx`

### csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Gmail</RootNamespace>
    <PackageId>Rag.NET.DataProviders.Gmail</PackageId>
    <Description>Gmail (IMAP via MailKit) data provider for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.DataProviders\Rag.NET.DataProviders.csproj" />
    <PackageReference Include="MailKit" Version="4.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.DataProviders.Gmail.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
```

### GmailOptions.cs

```csharp
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Gmail;

public sealed class GmailOptions : CloudStorageOptions
{
    /// <summary>
    /// IMAP search query forwarded to MailKit's <c>SearchQuery.FromString</c>.
    /// Defaults to <c>"in:inbox"</c>.
    /// </summary>
    public string Query      { get; init; } = "in:inbox";

    /// <summary>Maximum number of messages to yield per run.</summary>
    public int    MaxResults { get; init; } = 500;
}
```

### GmailDataProvider.cs

Uses MailKit via an `IImapClient` factory delegate so the constructor is fully testable with NSubstitute. Delta runs use a `UniqueId` watermark stored in `DeltaToken`: only messages with UID greater than the stored value are fetched.

Each message is yielded as one `FileHandle` with a Markdown representation: subject as heading, sender/date/recipient header block, and plain-text body (HTML stripped if no text part exists).

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Gmail;

public sealed class GmailDataProvider : FileContentProviderBase
{
    private readonly ITokenProvider    _tokenProvider;
    private readonly GmailOptions      _options;
    private readonly Func<IImapClient> _clientFactory;

    public GmailDataProvider(
        ITokenProvider tokenProvider,
        GmailOptions options,
        Func<IImapClient>? clientFactory = null)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
        _options       = options;
        _clientFactory = clientFactory ?? (() => new ImapClient());
    }

    protected override async IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var token  = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        using var client = _clientFactory();

        await client.ConnectAsync(
            "imap.gmail.com", 993,
            MailKit.Security.SecureSocketOptions.SslOnConnect,
            cancellationToken).ConfigureAwait(false);

        await client.AuthenticateAsync(
            new MailKit.Security.SaslMechanismOAuth2(client.AuthenticationMechanisms, token),
            cancellationToken).ConfigureAwait(false);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

        IList<UniqueId> uids;
        if (_options.DeltaToken is not null
            && UniqueId.TryParse(_options.DeltaToken, out var lastUid))
        {
            // Fetch only messages with UID strictly greater than the watermark
            uids = await inbox.SearchAsync(
                SearchQuery.Uids(new UniqueIdRange(
                    new UniqueId(lastUid.Id + 1), UniqueId.MaxValue)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            uids = await inbox.SearchAsync(
                SearchQuery.FromString(_options.Query),
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var uid in uids.Take(_options.MaxResults))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = await inbox.GetMessageAsync(uid, cancellationToken).ConfigureAwait(false);
            yield return ToHandle(uid, message);
        }

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private static FileHandle ToHandle(UniqueId uid, MimeMessage message)
    {
        var markdown    = ToMarkdown(message);
        var subject     = string.IsNullOrWhiteSpace(message.Subject)
            ? $"message-{uid}" : message.Subject;
        var safeSubject = string.Concat(subject.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        return new FileHandle(
            Id:               uid.ToString(),
            FileName:         $"{safeSubject}.md",
            ETag:             uid.ToString(),
            OpenContentAsync: _ => Task.FromResult<Stream>(
                new MemoryStream(Encoding.UTF8.GetBytes(markdown))));
    }

    private static string ToMarkdown(MimeMessage message)
    {
        var body = message.TextBody
            ?? System.Text.RegularExpressions.Regex.Replace(
                message.HtmlBody ?? string.Empty, "<[^>]+>", string.Empty);

        return $"""
            # {message.Subject}

            **From:** {message.From}  **Date:** {message.Date:R}  **To:** {message.To}

            {body.Trim()}
            """;
    }
}
```

### GmailDataProviderExtensions.cs

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Gmail;

public static class GmailDataProviderExtensions
{
    /// <summary>
    /// Registers <see cref="GmailDataProvider"/> as an <see cref="IFileContentProvider"/>.
    /// The caller supplies an <see cref="ITokenProvider"/> that returns a valid OAuth2 access
    /// token for the Gmail IMAP scope (<c>https://mail.google.com/</c>).
    /// </summary>
    public static IServiceCollection AddGmailDataProvider(
        this IServiceCollection services,
        ITokenProvider tokenProvider,
        Action<GmailOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        var opts = new GmailOptions();
        configure?.Invoke(opts);

        return services.AddSingleton<IFileContentProvider>(
            new GmailDataProvider(tokenProvider, opts));
    }
}
```

### Tests csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.DataProviders.Gmail.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.DataProviders.Gmail\Rag.NET.DataProviders.Gmail.csproj" />
  </ItemGroup>
</Project>
```

### GmailDataProviderTests.cs

MailKit's `IImapClient` is a genuine interface, so NSubstitute mocks work without any HTTP handler shim. `IMailFolder` is also an interface and can be substituted directly.

```csharp
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using NSubstitute;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Gmail;
using Xunit;

namespace Rag.NET.DataProviders.Gmail.Tests;

public sealed class GmailDataProviderTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static GmailDataProvider MakeProvider(
        IImapClient mockClient,
        GmailOptions? options = null)
        => new(
            new StaticTokenProvider("fake-token"),
            options ?? new GmailOptions(),
            clientFactory: () => mockClient);

    private static (IImapClient client, IMailFolder inbox) MakeMocks(
        List<UniqueId> uids, MimeMessage message)
    {
        var client = Substitute.For<IImapClient>();
        var inbox  = Substitute.For<IMailFolder>();

        client.Inbox.Returns(inbox);
        client.AuthenticationMechanisms
            .Returns(new HashSet<string>(StringComparer.Ordinal));

        inbox.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(uids);
        inbox.GetMessageAsync(Arg.Any<UniqueId>(), Arg.Any<CancellationToken>())
            .Returns(message);

        return (client, inbox);
    }

    private static MimeMessage MakeMessage(string subject = "Test Subject")
    {
        var msg = new MimeMessage();
        msg.Subject = subject;
        msg.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        msg.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        msg.Date = DateTimeOffset.UtcNow;
        msg.Body = new TextPart("plain") { Text = "Hello world" };
        return msg;
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsOneEntryPerMessage()
    {
        var message = MakeMessage();
        var (client, _) = MakeMocks([new UniqueId(1), new UniqueId(2)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, e => Assert.EndsWith(".md", e.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_FileName_DerivedFromSubject()
    {
        var message = MakeMessage("Invoice Q1-2026");
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("Invoice Q1-2026.md", results[0].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesAllEntries()
    {
        // All generated files are .md; filter for .txt must yield nothing.
        var message = MakeMessage();
        var (client, _) = MakeMocks([new UniqueId(1)], message);
        var sut = MakeProvider(client, new GmailOptions { Extensions = [".txt"] });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public void Constructor_NullTokenProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GmailDataProvider(null!, new GmailOptions()));
    }
}
```

Note: `StaticTokenProvider` is a test-only helper (an `ITokenProvider` that returns a fixed string) assumed to be defined in `tests/Rag.NET.DataProviders.Tests/` or a shared test utilities assembly and made visible via `InternalsVisibleTo` or a public class in the base test project.

### `Rag.NET.slnx` additions

Inside `<Folder Name="/src/">`:
```xml
<Project Path="src/Rag.NET.DataProviders.Gmail/Rag.NET.DataProviders.Gmail.csproj" />
```

Inside `<Folder Name="/tests/">`:
```xml
<Project Path="tests/Rag.NET.DataProviders.Gmail.Tests/Rag.NET.DataProviders.Gmail.Tests.csproj" />
```

### Commit

```bash
git add src/Rag.NET.DataProviders.Gmail/ tests/Rag.NET.DataProviders.Gmail.Tests/ Rag.NET.slnx
git commit -m "feat: add Rag.NET.DataProviders.Gmail connector"
```

---

## Task 8: Full solution build, run all new tests, update feature backlog

### Step 1: Build the entire solution

```bash
dotnet build
```

Expected outcome: **Build succeeded, 0 errors.** All seven new projects (Confluence, Jira, Notion, Asana from Part 1; Slack, MicrosoftTeams, Gmail from Part 2) must compile cleanly. Common failure modes to watch for:

- `Refit` source generator not running on older SDK — ensure `<LangVersion>` is at least `12` (inherited from `Directory.Build.props`).
- `Microsoft.Graph` v5 breaking changes vs v4 — verify `ChatMessage` model namespace is `Microsoft.Graph.Models`, not `Microsoft.Graph`.
- MailKit `UniqueIdRange` constructor — takes two `UniqueId` values; ensure `UniqueId.MaxValue` compiles (added in MailKit 4.x).

### Step 2: Run all new connector tests

Run each new test project individually to get per-project pass/fail isolation:

```bash
dotnet test tests/Rag.NET.DataProviders.Confluence.Tests/ -v minimal
dotnet test tests/Rag.NET.DataProviders.Jira.Tests/ -v minimal
dotnet test tests/Rag.NET.DataProviders.Notion.Tests/ -v minimal
dotnet test tests/Rag.NET.DataProviders.Asana.Tests/ -v minimal
dotnet test tests/Rag.NET.DataProviders.Slack.Tests/ -v minimal
dotnet test tests/Rag.NET.DataProviders.MicrosoftTeams.Tests/ -v minimal
dotnet test tests/Rag.NET.DataProviders.Gmail.Tests/ -v minimal
```

Expected outcome: **All pass, 0 failures** across all seven projects (4 tests each = 28 total).

Or run all at once:

```bash
dotnet test --filter "FullyQualifiedName~Rag.NET.DataProviders.Confluence|Rag.NET.DataProviders.Jira|Rag.NET.DataProviders.Notion|Rag.NET.DataProviders.Asana|Rag.NET.DataProviders.Slack|Rag.NET.DataProviders.MicrosoftTeams|Rag.NET.DataProviders.Gmail"
```

### Step 3: Update `docs/reference/features.md`

#### 3a: Mark Group 2 and Group 3 as Done

In the **Group 2 — Collaboration** section, add the status line immediately after the section header:

```markdown
#### Group 2 — Collaboration

**Status:** ✅ Done
```

In the **Group 3 — Communication** section, add the status line immediately after the section header:

```markdown
#### Group 3 — Communication

**Status:** ✅ Done
```

#### 3b: Update the priority table

Change the following rows from `[ ]` to `[x]` in the `## Priority / Dependencies` table at the bottom of the file:

| Old row | New row |
|---|---|
| `\| [ ] \| SaaS: Confluence \| Medium \| Confluence REST API \|` | `\| [x] \| SaaS: Confluence \| Medium \| Confluence REST API \|` |
| `\| [ ] \| SaaS: Notion \| Medium \| Notion REST API \|` | `\| [x] \| SaaS: Notion \| Medium \| Notion REST API \|` |
| `\| [ ] \| SaaS: Jira \| Medium \| Jira REST API \|` | `\| [x] \| SaaS: Jira \| Medium \| Jira REST API \|` |
| `\| [ ] \| SaaS: Asana \| Medium \| Asana REST API \|` | `\| [x] \| SaaS: Asana \| Medium \| Asana REST API \|` |
| `\| [ ] \| SaaS: Slack \| Medium \| Slack Web API \|` | `\| [x] \| SaaS: Slack \| Medium \| Slack Web API \|` |
| `\| [ ] \| SaaS: Gmail / IMAP \| Medium \| MailKit \|` | `\| [x] \| SaaS: Gmail / IMAP \| Medium \| MailKit \|` |
| `\| [ ] \| SaaS: Microsoft Teams \| Medium \| Microsoft Graph SDK \|` | `\| [x] \| SaaS: Microsoft Teams \| Medium \| Microsoft Graph SDK \|` |

Note: `SaaS: Airtable` remains `[ ]` — it is part of Group 2 but is **not** in scope for this implementation sprint.

### Step 4: Commit the feature backlog update

```bash
git add docs/reference/features.md
git commit -m "docs: mark Group 2 and Group 3 SaaS connectors as done"
```

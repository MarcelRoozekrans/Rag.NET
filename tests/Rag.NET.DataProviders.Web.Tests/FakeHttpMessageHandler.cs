using System.Net;

namespace Rag.NET.DataProviders.Web.Tests;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;
    private readonly Dictionary<string, HttpStatusCode> _statuses;

    /// <param name="responses">Bodies answered with 200, by URL.</param>
    /// <param name="statuses">
    /// URLs answered with a fixed status and no body, such as a robots.txt that returns 503 (#998).
    /// Every URL in neither map answers 404.
    /// </param>
    public FakeHttpMessageHandler(
        Dictionary<string, string> responses, Dictionary<string, HttpStatusCode>? statuses = null)
    {
        _responses = responses;
        _statuses = statuses ?? new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal);
    }

    /// <summary>
    /// How many requests this handler has been asked for.
    /// </summary>
    /// <remarks>
    /// Added for issue #252: the point of pruning a <c>&lt;sitemapindex&gt;</c> link is that the
    /// nested sitemap is <i>never fetched</i>, and the only way to assert an absent request is to
    /// count them. Asserting on the yielded entries alone cannot distinguish "pruned the link"
    /// from "followed it and filtered everything inside".
    /// </remarks>
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var url = request.RequestUri!.ToString();
        if (_statuses.TryGetValue(url, out var status))
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }

        if (_responses.TryGetValue(url, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

using System.Net;

namespace Rag.NET.DataProviders.Exchange.Tests;

/// <summary>
/// Intercepts outbound Graph SDK HTTP calls and returns canned payloads with a status code.
/// Full-URL match is attempted first; then the longest matching substring key wins
/// (most specific key takes precedence over shorter, more general keys).
/// All request URLs are recorded in <see cref="Requests"/> for laziness/filter assertions.
/// </summary>
internal sealed class FakeGraphHandler(Dictionary<string, (HttpStatusCode Status, string Body)> responses)
    : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);

        if (responses.TryGetValue(url, out var exact))
            return Task.FromResult(ToResponse(exact));

        string? bestKey = null;
        int     bestLen = -1;
        foreach (var k in responses.Keys)
        {
            if (url.Contains(k, StringComparison.Ordinal) && k.Length > bestLen)
            {
                bestKey = k;
                bestLen = k.Length;
            }
        }

        return Task.FromResult(bestKey is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : ToResponse(responses[bestKey]));
    }

    private static HttpResponseMessage ToResponse((HttpStatusCode Status, string Body) canned)
        => new(canned.Status)
        {
            Content = new StringContent(canned.Body, System.Text.Encoding.UTF8, "application/json"),
        };
}

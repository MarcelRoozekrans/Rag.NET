using System.Net;

namespace Rag.NET.DataProviders.Web.Tests;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;

    public FakeHttpMessageHandler(Dictionary<string, string> responses)
        => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
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

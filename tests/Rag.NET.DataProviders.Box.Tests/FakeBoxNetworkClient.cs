using Box.Sdk.Gen;

namespace Rag.NET.DataProviders.Box.Tests;

/// <summary>
/// Drives <see cref="BoxClient"/> through <c>NetworkSession.WithNetworkClient</c> instead of a
/// real HTTP transport. <see cref="Box.Sdk.Gen.INetworkClient"/> is a one-method seam
/// (<c>FetchAsync(FetchOptions) -&gt; Task&lt;FetchResponse&gt;</c>), so a canned JSON response
/// per call is enough to drive the generated managers' own (de)serialization — this makes the
/// enumeration paths in <see cref="BoxDataProvider"/> reachable from a unit test, which the
/// hand-written 3.x SDK's concrete <c>BoxClient</c> never allowed.
/// </summary>
internal sealed class FakeBoxNetworkClient(Func<FetchOptions, FetchResponse> handle) : INetworkClient
{
    public Task<FetchResponse> FetchAsync(FetchOptions options) => Task.FromResult(handle(options));

    /// <summary>Builds a 200 OK JSON response body, the shape every Box read endpoint returns.</summary>
    public static FetchResponse Json(string body)
        => new(200, new Dictionary<string, string>(StringComparer.Ordinal))
        {
            Data = JsonUtils.JsonToSerializedData(body),
        };

    /// <summary>Builds a 200 OK raw-content response, the shape the download endpoint returns.</summary>
    public static FetchResponse Content(string body)
        => new(200, new Dictionary<string, string>(StringComparer.Ordinal))
        {
            Content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)),
        };

    public static BoxClient MakeClient(Func<FetchOptions, FetchResponse> handle)
    {
        var session = new NetworkSession().WithNetworkClient(new FakeBoxNetworkClient(handle));
        return new BoxClient(new BoxDeveloperTokenAuth("fake-token"), session);
    }
}

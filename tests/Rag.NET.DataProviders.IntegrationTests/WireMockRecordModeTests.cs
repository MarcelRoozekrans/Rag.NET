using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

/// <summary>
/// Tests of the recording half of <see cref="WireMockServerFixture"/> itself, driven against a
/// stub upstream so no real service is involved.
/// </summary>
/// <remarks>
/// <para>
/// #283 asks contributors to point this harness at a real account with a real credential. The
/// fixture's defence is <c>VolatileRequestHeaders</c>, which tells the proxy which request headers
/// not to write into the cassette — and until these tests existed, nothing anywhere established
/// that the list has that effect. Asserting a string is a member of an array would not: it
/// restates the implementation and would keep passing if WireMock stopped honouring
/// <c>ExcludedHeaders</c>, or honoured it case-sensitively.
/// </para>
/// <para>
/// So each test records a real exchange through the proxy and reads what landed on disk. Each one
/// also asserts the recording <i>happened</i> — a recorded file, matching the path that was
/// requested — because "the secret is absent" is trivially true of a cassette that was never
/// written, which is the shape of vacuous guard this repository keeps deleting.
/// </para>
/// </remarks>
public sealed class WireMockRecordModeTests
{
    /// <summary>Recognisable, and shaped like a real Azure key: 32 lower-case hex characters.</summary>
    private const string SecretValue = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// The credential headers Rag.NET's own dependencies send. <c>Ocp-Apim-Subscription-Key</c> is
    /// what <c>AzureKeyCredential</c> puts on every Document Intelligence request; <c>api-key</c>
    /// is Azure AI Search's and Qdrant's. Both casings of the first are exercised because the
    /// exclusion list carries one spelling and the SDK is under no obligation to match it.
    /// </summary>
    [Theory]
    [InlineData("Ocp-Apim-Subscription-Key")]
    [InlineData("ocp-apim-subscription-key")]
    [InlineData("api-key")]
    public async Task RecordMode_DoesNotWriteACredentialHeaderIntoTheCassette(string headerName)
    {
        using var upstream = WireMockServer.Start();
        upstream
            .Given(Request.Create().WithPath("/probe").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"ok\":true}"));

        var recorded = await RecordOneExchangeAsync(upstream.Url!, headerName);

        Assert.Contains("/probe", recorded, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, recorded, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Proxies one request carrying <paramref name="headerName"/> through a fixture in record
    /// mode and returns every mapping it wrote, concatenated.
    /// </summary>
    private static async Task<string> RecordOneExchangeAsync(string upstreamUrl, string headerName)
    {
        var cassettePath = Path.Combine(
            Path.GetTempPath(), "ragnet-record-" + Guid.NewGuid().ToString("N"));

        var fixture = new WireMockServerFixture();
        await fixture.InitializeAsync();
        try
        {
            fixture.LoadCassettesFrom(cassettePath, upstreamUrl, recordMode: true);

            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, fixture.BaseUrl + "/probe");
            request.Headers.Add(headerName, SecretValue);
            using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();

            return await ReadRecordedMappingsAsync(cassettePath);
        }
        finally
        {
            await fixture.DisposeAsync();
            TryDelete(cassettePath);
        }
    }

    /// <summary>
    /// Reads the mappings the proxy wrote, waiting for them: WireMock writes the file while
    /// answering the request, so it can lag the response the client already has.
    /// </summary>
    private static async Task<string> ReadRecordedMappingsAsync(string cassettePath)
    {
        var mappings = Path.Combine(cassettePath, "__admin", "mappings");

        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (Directory.Exists(mappings))
            {
                var files = Directory.GetFiles(mappings, "*.json");
                if (files.Length > 0)
                {
                    var contents = new string[files.Length];
                    for (var i = 0; i < files.Length; i++)
                    {
                        contents[i] = File.ReadAllText(files[i]);
                    }

                    return string.Join("\n", contents);
                }
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return string.Empty;   // the "/probe" assertion reports this as the failure it is
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a run over.
        }
    }
}

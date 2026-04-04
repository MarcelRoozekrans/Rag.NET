using WireMock.Server;
using WireMock.Settings;
using Xunit;

namespace Rag.NET.Testing;

/// <summary>
/// Provides a WireMock.Net server for recording and replaying HTTP interactions.
///
/// Record mode: set WIREMOCK_RECORD=true and run tests against real APIs.
/// Cassettes saved under tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/{connector}/
/// Replay mode (default): loads cassettes from disk — no network traffic.
/// </summary>
public sealed class WireMockServerFixture : IAsyncLifetime
{
    private static readonly bool RecordMode =
        string.Equals(
            Environment.GetEnvironmentVariable("WIREMOCK_RECORD"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public WireMockServer Server { get; private set; } = null!;

    public string BaseUrl => Server.Url!;

    public ValueTask InitializeAsync()
    {
        Server = WireMockServer.Start(new WireMockServerSettings { UseSSL = false });
        return ValueTask.CompletedTask;
    }

    public void LoadCassettes(string connectorName)
    {
        var path = GetCassettePath(connectorName);
        if (Directory.Exists(path))
            Server.ReadStaticMappings(path);
    }

    public void SaveCassettes(string connectorName)
    {
        if (!RecordMode) return;
        var path = GetCassettePath(connectorName);
        Directory.CreateDirectory(path);
        Server.SaveStaticMappings(path);
    }

    public static string GetCassettePath(string connectorName) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "Cassettes",
            connectorName);

    public ValueTask DisposeAsync()
    {
        Server.Dispose();
        return ValueTask.CompletedTask;
    }
}

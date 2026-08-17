using WireMock.Handlers;
using WireMock.Server;
using WireMock.Settings;
using Xunit;

namespace Rag.NET.Testing;

/// <summary>
/// Provides a WireMock.Net server for recording and replaying HTTP interactions.
/// </summary>
/// <remarks>
/// <para>
/// Replay mode (default) loads cassettes from disk and makes no network calls. Record mode —
/// <c>WIREMOCK_RECORD=true</c> — proxies to the real service and writes what comes back.
/// Cassettes live under <c>tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/{connector}/</c>.
/// </para>
/// <para>
/// <b>Record mode had never worked, and it failed in the one way that hides itself.</b> WireMock's
/// <c>SaveMappingToFile</c> writes through the <see cref="LocalFileSystemHandler"/>, which appends
/// its own <c>__admin/mappings</c> segment — so recordings landed in
/// <c>Cassettes/{connector}/__admin/mappings/</c> while <see cref="ReplayFrom"/> read
/// <c>Cassettes/{connector}/</c>. Nothing anyone recorded was ever replayed.
/// </para>
/// <para>
/// <c>SaveCassettes</c> would have written flat, into the directory replay reads. It had
/// <b>zero callers</b> against 18 for <see cref="LoadCassettes"/>: the record half of this harness
/// was written and never wired up.
/// </para>
/// <para>
/// The failure is invisible at the moment it happens. In record mode every request is proxied to the
/// real service, so the tests <i>pass</i> — a contributor sees green, commits, and the replay run
/// fails later with no indication that the cause is a path. #283 asks eighteen people to follow this
/// workflow, and its troubleshooting note ("often a timestamp or a nonce") would have sent every one
/// of them after the wrong thing.
/// </para>
/// <para>
/// <b>And the path was only the first of two defects.</b> The proxy records every request header as
/// a match condition, including <c>Host</c> — whose recorded value is <c>localhost:{ephemeral
/// port}</c>. WireMock binds a fresh port each run, so a recorded mapping could never match again
/// <i>even on the machine that made it</i>. Fixing the path alone still left every test failing on
/// replay with a bare <c>404</c>. See <see cref="VolatileRequestHeaders"/>.
/// </para>
/// <para>
/// Both are fixed, and record → replay was verified end to end against the real GitHub API. Replay
/// reads the recorded location as well as the flat one, and <see cref="FlattenRecordings"/> lifts
/// recordings into the flat directory on the next run — automatically, because the predecessor of
/// that helper was a file-mover nobody called.
/// </para>
/// </remarks>
public sealed class WireMockServerFixture : IAsyncLifetime
{
    /// <summary>WireMock's own layout under a <see cref="LocalFileSystemHandler"/> root.</summary>
    private const string RecordedSubdirectory = "__admin";

    /// <summary>
    /// Request headers a recording must not turn into match conditions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By default the proxy records <b>every</b> request header as a matcher, and two of them make
    /// the resulting cassette unreplayable anywhere:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>Host</c> — the recorded value is <c>localhost:{ephemeral port}</c>. WireMock binds a fresh
    /// port on every run, so the matcher cannot match again <i>even on the machine that recorded
    /// it</i>. This is the one that made record mode useless.
    /// </item>
    /// <item>
    /// <c>User-Agent</c> — Octokit's contains the OS build, architecture, culture and library
    /// version (observed: <c>Win32NT 10.0.26200; amd64; nl-NL; Octokit.net 14.0.0+7fa5b0f</c>). A
    /// matcher on that only matches the recorder's machine, and it commits their locale and OS
    /// build into a fixture as a side effect.
    /// </item>
    /// </list>
    /// <para>
    /// <c>Authorization</c> and <c>Cookie</c> are here for a different reason: the harness should not
    /// write a credential to disk at all. #283 asks contributors to open every recorded file and
    /// remove the token by hand, and notes that some will leak one anyway. Not recording it is a
    /// better guarantee than remembering to delete it, and <c>CassetteSecretTests</c> stays as the
    /// backstop rather than the only defence.
    /// </para>
    /// <para>
    /// <b><c>Accept</c> is deliberately absent from this list.</b> It is semantic here, not volatile:
    /// GitHub distinguishes raw file content from JSON metadata on the same path by
    /// <c>Accept: application/vnd.github.v3.raw</c>, so a cassette that stopped matching on it would
    /// answer a metadata request with a file body. Excluding headers wholesale would trade one broken
    /// recording for another.
    /// </para>
    /// </remarks>
    private static readonly string[] VolatileRequestHeaders =
    [
        "Host",
        "User-Agent",
        "Accept-Encoding",
        "Authorization",
        "Cookie",
        "Connection",
        "Content-Length",
        "Date",
        "traceparent",
        "tracestate",
        "Request-Id",
        "X-Request-Id",
    ];

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

    public void LoadCassettes(string connectorName, string? proxyBaseUrl = null)
    {
        Server.ResetMappings();          // clear all previous stubs before loading new ones
        var path = GetCassettePath(connectorName);

        if (RecordMode && proxyBaseUrl is not null)
        {
            // Restart on the same port so BaseUrl remains valid for tests that captured it.
            var port = Server.Ports[0];
            Server.Dispose();
            Directory.CreateDirectory(path);
            Server = WireMockServer.Start(new WireMockServerSettings
            {
                UseSSL = false,
                Port = port,
                FileSystemHandler = new LocalFileSystemHandler(path),
                ProxyAndRecordSettings = new ProxyAndRecordSettings
                {
                    Url = proxyBaseUrl,
                    SaveMapping = true,
                    SaveMappingToFile = true,
                    ExcludedHeaders = VolatileRequestHeaders,
                },
            });
        }
        else
        {
            ReplayFrom(path);
        }
    }

    /// <summary>
    /// Loads every committed and every freshly recorded mapping for a connector.
    /// </summary>
    /// <remarks>
    /// Both layouts, and the recorded one is not optional: it is where record mode actually writes.
    /// A single <c>ReadStaticMappings(path)</c> silently loads nothing when the only cassettes
    /// present are recorded ones, which is the defect this method exists to close.
    /// </remarks>
    /// <param name="path">The connector's cassette directory.</param>
    private void ReplayFrom(string path)
    {
        // Converge on one layout. A recording lands in __admin/mappings; the next replay run lifts it
        // into the flat directory the committed cassettes already use, so a contributor ends up with
        // the path docs/reference and #283 both name. No-op once there is nothing to move, which is
        // every CI run.
        _ = FlattenRecordings(path);

        if (Directory.Exists(path))
        {
            Server.ReadStaticMappings(path);
        }

        // Still read the recorded location: flattening can be blocked by a file lock, and silently
        // loading nothing is the failure this whole method exists to stop.
        var recorded = RecordedMappingsPath(path);
        if (Directory.Exists(recorded) && Directory.EnumerateFiles(recorded, "*.json").Any())
        {
            Server.ReadStaticMappings(recorded);
        }
    }

    private static string RecordedMappingsPath(string cassettePath) =>
        Path.Combine(cassettePath, RecordedSubdirectory, "mappings");

    /// <summary>
    /// Flattens freshly recorded mappings into the connector's cassette directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from <see cref="ReplayFrom"/> rather than left for someone to invoke, because the
    /// method this replaces — <c>SaveCassettes</c> — was the only thing that put cassettes where
    /// replay looked and had zero callers for the life of the harness. A file-moving helper nobody
    /// calls is indistinguishable from a broken one.
    /// </para>
    /// <para>
    /// One flat directory reviews better in a diff than a <c>__admin/mappings</c> tree, and the
    /// hand-written cassettes are already flat. Failures are swallowed on purpose: a locked file
    /// should degrade to replaying from the recorded location, not fail a test run over tidying.
    /// </para>
    /// </remarks>
    /// <param name="cassettePath">The connector's cassette directory.</param>
    /// <returns>The number of files moved.</returns>
    private static int FlattenRecordings(string cassettePath)
    {
        var recorded = RecordedMappingsPath(cassettePath);
        if (!Directory.Exists(recorded))
        {
            return 0;
        }

        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(recorded, "*.json"))
        {
            try
            {
                File.Move(file, Path.Combine(cassettePath, Path.GetFileName(file)), overwrite: true);
                moved++;
            }
            catch (IOException)
            {
                // Left where it is; ReplayFrom reads that location too.
            }
        }

        return moved;
    }

    public static string GetCassettePath(string connectorName) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",    // up to tests/Rag.NET.DataProviders.IntegrationTests/
            "Cassettes",
            connectorName);

    public ValueTask DisposeAsync()
    {
        Server.Dispose();
        return ValueTask.CompletedTask;
    }
}

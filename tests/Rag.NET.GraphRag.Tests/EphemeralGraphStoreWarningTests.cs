using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Graph;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// An unconfigured graph store still works, and no longer does so silently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>UseGraphRag()</c> without a graph store defaults to an in-memory
/// SQLite database. That graph is discarded when the process exits, and the next ingest rebuilds it
/// — <b>22 m 18 s</b> on the MultiHop-RAG corpus, measured. Nothing said so: the default was set by
/// a one-line <c>else</c> branch, carried no log, and appeared on no published page.
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/298">#298</see> listed fixing that
/// as the cheap step of its four, separable from the backend question it was actually about.
/// </para>
/// <para>
/// <b>A warning, not a refusal.</b> Refusing to start without an explicit store would tax every
/// quick experiment to fix a production concern. The default stays convenient; it stops being
/// invisible. These tests pin both halves, because a warning that broke the default would be worse
/// than the silence it replaced.
/// </para>
/// </remarks>
public sealed class EphemeralGraphStoreWarningTests
{
    /// <summary>The unconfigured default warns that the graph will not survive the process.</summary>
    [Fact]
    public async Task AnUnconfiguredGraphStore_WarnsThatTheGraphIsEphemeral()
    {
        var logger = new CountingLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<GraphStoreBuilder>>(logger);

        _ = new GraphStoreBuilder(services).UseEphemeralInMemory();
        await using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<IGraphStore>();

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("log_ephemeral_graph_store", warning.EventName, StringComparer.Ordinal);
        Assert.Contains("discarded", warning.Message, StringComparison.Ordinal);
        Assert.Contains("UseSqlite", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The warning names the remedy, not merely the problem.
    /// </summary>
    /// <remarks>
    /// A reader who hits this at 2am needs the call that fixes it, in the message. Telling them
    /// only that the graph is ephemeral leaves them to find <c>UseSqlite</c> themselves, which is
    /// how a warning becomes noise people filter out.
    /// </remarks>
    [Fact]
    public async Task TheWarningNamesTheCallThatFixesIt()
    {
        var logger = new CountingLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<GraphStoreBuilder>>(logger);

        _ = new GraphStoreBuilder(services).UseEphemeralInMemory();
        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IGraphStore>();

        Assert.Contains("UseGraphRag(graph:", logger.Entries[0].Message, StringComparison.Ordinal);
    }

    /// <summary>Once per container, not once per resolution.</summary>
    /// <remarks>
    /// The registration is a singleton, so the factory runs once. Asserted rather than assumed: a
    /// per-resolution warning would fire on every ingest and be muted within a day, taking the
    /// signal with it.
    /// </remarks>
    [Fact]
    public async Task TheWarningIsLoggedOnce_NotPerResolution()
    {
        var logger = new CountingLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<GraphStoreBuilder>>(logger);

        _ = new GraphStoreBuilder(services).UseEphemeralInMemory();
        await using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<IGraphStore>();
        _ = provider.GetRequiredService<IGraphStore>();
        _ = provider.GetRequiredService<IGraphStore>();

        _ = Assert.Single(logger.Entries);
    }

    /// <summary>An explicitly configured store warns about nothing.</summary>
    /// <remarks>
    /// The caller has already made the decision the warning exists to prompt, so repeating it would
    /// be the noise this guards against.
    /// </remarks>
    [Fact]
    public async Task AnExplicitlyConfiguredStore_DoesNotWarn()
    {
        var logger = new CountingLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<GraphStoreBuilder>>(logger);

        var file = Path.Combine(Path.GetTempPath(), $"ragnet-graph-{Guid.NewGuid():N}.db");
        try
        {
            _ = new GraphStoreBuilder(services).UseSqlite(file);
            await using var provider = services.BuildServiceProvider();

            _ = provider.GetRequiredService<IGraphStore>();

            Assert.Empty(logger.Entries);

            // Dispose before the finally deletes the file: the store holds the connection open.
            await provider.DisposeAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>The default still resolves a working store — a warning must not become a break.</summary>
    [Fact]
    public async Task TheDefaultStillResolvesAUsableStore()
    {
        var services = new ServiceCollection();
        _ = new GraphStoreBuilder(services).UseEphemeralInMemory();
        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IGraphStore>());
    }

    /// <summary>Counts every entry, so once-ness can be asserted rather than mere presence.</summary>
    private sealed class CountingLogger : ILogger<GraphStoreBuilder>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string? EventName, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string? EventName, string Message)> Entries
        {
            get { lock (_gate) return [.. _entries]; }
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var entry = (logLevel, eventId.Name, formatter(state, exception));
            lock (_gate) _entries.Add(entry);
        }
    }
}

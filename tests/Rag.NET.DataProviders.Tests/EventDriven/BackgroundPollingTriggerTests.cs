using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.EventDriven;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Tests.EventDriven;

public sealed class BackgroundPollingTriggerTests
{
    /// <summary>
    /// Empty provider counting <see cref="GetFilesAsync"/> calls (= polling cycles) and
    /// signalling a TCS when the second cycle starts — deterministic, no sleep-polling.
    /// </summary>
    private sealed class FakeProvider(bool throwOnFirstCall = false) : IFileContentProvider
    {
        private int _calls;
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstCallStarted => _firstCall.Task;

        public Task SecondCallStarted => _secondCall.Task;

        public int Calls => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            _firstCall.TrySetResult();
            if (call >= 2)
                _secondCall.TrySetResult();
            if (throwOnFirstCall && call == 1)
                throw new InvalidOperationException("cycle 1 failure");
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private static PollingIngestionOptions FastOptions() => new()
    {
        ProviderId = "test-provider",
        PollingInterval = TimeSpan.FromMilliseconds(20),
    };

    [Fact]
    public async Task ExecuteAsync_RunsRepeatedCycles()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeProvider();
        var sut = new BackgroundPollingTrigger(Substitute.For<IRagPipeline>(), provider, FastOptions());

        await sut.StartAsync(ct);
        try
        {
            await provider.SecondCallStarted.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.True(provider.Calls >= 2);
    }

    [Fact]
    public async Task ExecuteAsync_FirstCycleFails_SecondCycleStillRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeProvider(throwOnFirstCall: true);
        var sut = new BackgroundPollingTrigger(Substitute.For<IRagPipeline>(), provider, FastOptions());

        await sut.StartAsync(ct);
        try
        {
            await provider.SecondCallStarted.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.True(provider.Calls >= 2);
    }

    [Fact]
    public async Task StopAsync_ExitsCleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeProvider();
        var options = FastOptions();
        options.PollingInterval = TimeSpan.FromHours(1); // park the trigger in Task.Delay
        var sut = new BackgroundPollingTrigger(Substitute.For<IRagPipeline>(), provider, options);

        await sut.StartAsync(ct);
        // On .NET 10, StartAsync only SCHEDULES ExecuteAsync — stopping immediately would race
        // (a cancel that wins before ExecuteAsync starts leaves ExecuteTask Canceled, not
        // RanToCompletion). Wait until cycle 1 is provably underway before requesting the stop.
        await provider.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10), ct);
        // Cancelling mid-delay must exit promptly via the clean-shutdown OCE catch.
        await sut.StopAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.True(sut.ExecuteTask is { IsCompletedSuccessfully: true });
    }

    [Fact]
    public async Task ExecuteAsync_PassesCleanupModeThroughToProviderIngestion()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new FakeProvider();
        var hashStore = Substitute.For<IContentHashStore>();
        hashStore.GetAllIdsAsync(Arg.Any<ProviderId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<EntryId>>(new HashSet<EntryId>()));
        var options = FastOptions();
        options.CleanupMode = CleanupMode.Full;
        var sut = new BackgroundPollingTrigger(
            Substitute.For<IRagPipeline>(), provider, options, hashStore);

        await sut.StartAsync(ct);
        try
        {
            // IngestFromProviderAsync fetches known ids (CleanupMode.Full only) BEFORE
            // enumerating the provider, so once cycle 1 started the call has happened.
            await provider.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        // GetAllIdsAsync is only invoked when CleanupMode.Full reaches
        // IngestFromProviderAsync — observable proof of the pass-through.
        await hashStore.Received().GetAllIdsAsync(
            Arg.Is<ProviderId>(p => p!.Value == "test-provider"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UsePollingIngestion_UndefinedCleanupMode_Throws()
    {
        var builder = new RagBuilder(new ServiceCollection());

        var ex = Assert.Throws<ArgumentException>(() =>
            builder.UsePollingIngestion(_ => new FakeProvider(), o =>
            {
                o.ProviderId = "provider-1";
                o.CleanupMode = (CleanupMode)42;
            }));
        Assert.Contains("CleanupMode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsePollingIngestion_TwoRegistrations_YieldTwoIndependentHostedServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRagPipeline>());
        var builder = new RagBuilder(services);

        builder.UsePollingIngestion(_ => new FakeProvider(), o => o.ProviderId = "provider-1");
        builder.UsePollingIngestion(_ => new FakeProvider(), o =>
        {
            o.ProviderId = "provider-2";
            o.PollingInterval = TimeSpan.FromSeconds(1);
        });

        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.Equal(2, hosted.Count);
        Assert.All(hosted, h => Assert.IsType<BackgroundPollingTrigger>(h));
        Assert.NotSame(hosted[0], hosted[1]);
    }

    [Fact]
    public void UsePollingIngestion_EmptyProviderId_Throws()
    {
        var builder = new RagBuilder(new ServiceCollection());

        Assert.Throws<ArgumentException>(() =>
            builder.UsePollingIngestion(_ => new FakeProvider(), o => o.PollingInterval = TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void UsePollingIngestion_NonPositiveInterval_Throws()
    {
        var builder = new RagBuilder(new ServiceCollection());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UsePollingIngestion(_ => new FakeProvider(), o =>
            {
                o.ProviderId = "provider-1";
                o.PollingInterval = TimeSpan.Zero;
            }));
    }
}

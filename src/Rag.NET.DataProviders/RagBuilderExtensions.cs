using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders.EventDriven;
using Rag.NET.Models.Options;

namespace Rag.NET.DataProviders;

/// <summary>Event-driven ingestion registrations for the RAG builder.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers the in-memory bounded <see cref="IIngestionJobQueue"/>
    /// (<see cref="ChannelIngestionJobQueue"/>) and the <see cref="IngestionJobProcessor"/>
    /// hosted service that drains it. Producers (webhook endpoints, message-bus triggers)
    /// enqueue <see cref="Rag.NET.Models.IngestionJob"/>s; the processor ingests them via
    /// the registered <see cref="IIngestor"/>. Idempotent: queue and options use
    /// <c>TryAdd</c> and the processor is deduped by <c>AddHostedService</c>, so a second
    /// call is a no-op (the first registration's options win — no orphaned channel).
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">Configures the <see cref="EventDrivenIngestionOptions"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="EventDrivenIngestionOptions.QueueCapacity"/> is not positive.
    /// </exception>
    public static TBuilder UseEventDrivenIngestion<TBuilder>(
        this TBuilder builder,
        Action<EventDrivenIngestionOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new EventDrivenIngestionOptions();
        configure?.Invoke(opts);

        if (opts.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                $"EventDrivenIngestionOptions.QueueCapacity ({opts.QueueCapacity}) must be greater than zero.");
        }

        builder.Services.TryAddSingleton(opts);
        builder.Services.TryAddSingleton<IIngestionJobQueue>(
            sp => new ChannelIngestionJobQueue(sp.GetRequiredService<EventDrivenIngestionOptions>()));
        builder.Services.AddHostedService<IngestionJobProcessor>();
        return builder;
    }

    /// <summary>
    /// Registers a <see cref="BackgroundPollingTrigger"/> hosted service that re-ingests the
    /// provider created by <paramref name="providerFactory"/> every
    /// <see cref="PollingIngestionOptions.PollingInterval"/>. Hash-skip applies automatically
    /// when an <see cref="IContentHashStore"/> is registered. Each call registers an
    /// INDEPENDENT poller with its own provider and options instance — multiple registrations
    /// never collide.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="providerFactory">Creates the <see cref="IFileContentProvider"/> to poll.</param>
    /// <param name="configure">
    /// Required; must set a non-empty <see cref="PollingIngestionOptions.ProviderId"/>.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <see cref="PollingIngestionOptions.ProviderId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <see cref="PollingIngestionOptions.PollingInterval"/> is not positive.</exception>
    public static TBuilder UsePollingIngestion<TBuilder>(
        this TBuilder builder,
        Func<IServiceProvider, IFileContentProvider> providerFactory,
        Action<PollingIngestionOptions> configure)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(configure);

        var opts = new PollingIngestionOptions();
        configure(opts);

        if (opts.PollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                $"PollingIngestionOptions.PollingInterval ({opts.PollingInterval}) must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(opts.ProviderId))
        {
            throw new ArgumentException(
                "PollingIngestionOptions.ProviderId must be a non-empty string — it scopes content-hash bookkeeping for this poller.",
                nameof(configure));
        }

        if (!Enum.IsDefined(opts.CleanupMode))
        {
            throw new ArgumentException(
                $"PollingIngestionOptions.CleanupMode ({opts.CleanupMode}) is not a defined CleanupMode value.",
                nameof(configure));
        }

        // Deliberately NOT AddSingleton(opts) + AddHostedService<T>: each registration closes
        // over its own options and provider factory, so multiple pollers coexist without
        // sharing (or overwriting) a singleton options instance.
        builder.Services.AddSingleton<IHostedService>(sp => new BackgroundPollingTrigger(
            sp.GetRequiredService<IRagPipeline>(),
            providerFactory(sp),
            opts,
            sp.GetService<IContentHashStore>(),
            sp.GetService<ILogger<BackgroundPollingTrigger>>()));
        return builder;
    }
}

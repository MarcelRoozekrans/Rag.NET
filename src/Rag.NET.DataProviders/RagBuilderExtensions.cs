using Microsoft.Extensions.DependencyInjection;
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
    /// the registered <see cref="IIngestor"/>.
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
                $"QueueCapacity ({opts.QueueCapacity}) must be greater than zero.");
        }

        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<IIngestionJobQueue>(new ChannelIngestionJobQueue(opts));
        builder.Services.AddHostedService<IngestionJobProcessor>();
        return builder;
    }
}

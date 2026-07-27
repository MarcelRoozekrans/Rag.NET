using Azure.Core;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;

namespace Rag.NET.Ingestion.AzureServiceBus;

/// <summary>Service Bus ingestion registrations for the RAG builder.</summary>
public static class ServiceBusIngestionBuilderExtensions
{
    /// <summary>
    /// Registers an <see cref="AzureServiceBusIngestionTrigger"/> over a queue or subscription
    /// reached with a connection string.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="connectionString">
    /// A Service Bus connection string. Parsed immediately, so a malformed one fails at
    /// registration rather than at first receive.
    /// </param>
    /// <param name="queueName">
    /// The queue name, or the topic name when
    /// <see cref="ServiceBusIngestionOptions.SubscriptionName"/> is configured.
    /// </param>
    /// <param name="configure">Optional tuning for this registration.</param>
    /// <exception cref="ArgumentException">An argument or option is empty or malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric option is out of range.</exception>
    public static TBuilder UseServiceBusIngestion<TBuilder>(
        this TBuilder builder,
        string connectionString,
        string queueName,
        Action<ServiceBusIngestionOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);

        var options = BuildOptions(configure);
        return Register(builder, new ServiceBusClient(connectionString), queueName, options);
    }

    /// <summary>
    /// Registers an <see cref="AzureServiceBusIngestionTrigger"/> over a queue or subscription
    /// reached with a <see cref="TokenCredential"/> (OAuth / managed identity).
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="fullyQualifiedNamespace">
    /// The namespace host, e.g. <c>contoso.servicebus.windows.net</c>.
    /// </param>
    /// <param name="credential">The credential used to authenticate.</param>
    /// <param name="queueName">
    /// The queue name, or the topic name when
    /// <see cref="ServiceBusIngestionOptions.SubscriptionName"/> is configured.
    /// </param>
    /// <param name="configure">Optional tuning for this registration.</param>
    /// <exception cref="ArgumentException">An argument or option is empty or malformed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric option is out of range.</exception>
    public static TBuilder UseServiceBusIngestion<TBuilder>(
        this TBuilder builder,
        string fullyQualifiedNamespace,
        TokenCredential credential,
        string queueName,
        Action<ServiceBusIngestionOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedNamespace);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);

        var options = BuildOptions(configure);
        var client = new ServiceBusClient(fullyQualifiedNamespace, credential);
        return Register(builder, client, queueName, options);
    }

    /// <remarks>
    /// Deliberately not <c>AddSingleton(options)</c> plus <c>AddHostedService&lt;T&gt;</c>:
    /// each registration closes over its own client and options instance, so two queues — or
    /// one queue with sessions and another without — coexist without sharing or overwriting a
    /// singleton. This is the shape <c>UsePollingIngestion</c> uses, for the same reason.
    /// </remarks>
    private static TBuilder Register<TBuilder>(
        TBuilder builder, ServiceBusClient client, string entityName, ServiceBusIngestionOptions options)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IHostedService>(sp => new AzureServiceBusIngestionTrigger(
            client,
            sp.GetRequiredService<IIngestor>(),
            entityName,
            options,
            sp.GetService<ILogger<AzureServiceBusIngestionTrigger>>()));
        return builder;
    }

    private static ServiceBusIngestionOptions BuildOptions(Action<ServiceBusIngestionOptions>? configure)
    {
        var options = new ServiceBusIngestionOptions();
        configure?.Invoke(options);

        if (options.SubscriptionName is not null && string.IsNullOrWhiteSpace(options.SubscriptionName))
        {
            throw new ArgumentException(
                "ServiceBusIngestionOptions.SubscriptionName must be null (a queue) or a non-empty subscription name.",
                nameof(configure));
        }

        if (options.MaxConcurrentCalls <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                $"ServiceBusIngestionOptions.MaxConcurrentCalls ({options.MaxConcurrentCalls}) must be greater than zero.");
        }

        if (options.MaxConcurrentSessions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                $"ServiceBusIngestionOptions.MaxConcurrentSessions ({options.MaxConcurrentSessions}) must be greater than zero.");
        }

        if (options.PrefetchCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                $"ServiceBusIngestionOptions.PrefetchCount ({options.PrefetchCount}) must not be negative.");
        }

        if (options.MaxAutoLockRenewalDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                $"ServiceBusIngestionOptions.MaxAutoLockRenewalDuration ({options.MaxAutoLockRenewalDuration}) must not be negative.");
        }

        return options;
    }
}

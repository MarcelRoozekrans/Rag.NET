using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion.AzureServiceBus.Logging;

namespace Rag.NET.Ingestion.AzureServiceBus;

/// <summary>
/// Consumes a Service Bus queue or subscription, ingests each message end to end through
/// <see cref="IIngestor"/>, and settles it on the outcome: complete on success, abandon on a
/// transient failure so the broker redelivers, dead-letter with a reason on a permanent one.
/// </summary>
/// <remarks>
/// <para>
/// It implements <see cref="IHostedService"/> and <see cref="IAsyncDisposable"/> rather than
/// deriving from <c>BackgroundService</c>. There is no loop to run: the SDK's processor owns
/// the receive pump and only wants start and stop. What there <i>is</i> is ownership of two
/// async-disposable resources, the processor and the client, which <c>BackgroundService</c>
/// has no way to express.
/// </para>
/// <para>
/// It does not route through <c>IIngestionJobQueue</c>. That queue is an in-memory bounded
/// channel with no persistence, so a producer that enqueued and settled would convert
/// at-least-once delivery into at-most-once the moment the host crashed with the channel
/// non-empty. <c>BackgroundPollingTrigger</c> bypasses the queue for the same reason: a
/// trigger that owns its ingestion can report what happened, and one that hands work off
/// cannot.
/// </para>
/// <para>
/// Register with <c>UseServiceBusIngestion</c>. The public constructor is the escape hatch for
/// a caller who wants to configure <see cref="ServiceBusClient"/> themselves (custom retry
/// policy, proxy, alternate transport); the trigger takes ownership of the client it is given
/// and disposes it.
/// </para>
/// </remarks>
public sealed class AzureServiceBusIngestionTrigger : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusProcessor? _processor;
    private readonly ServiceBusSessionProcessor? _sessionProcessor;
    private readonly ServiceBusMessageIngestionHandler _handler;
    private readonly ILogger _logger;
    private readonly string _entityPath;
    private readonly bool _sessionsEnabled;
    private int _disposed;

    /// <summary>Creates a trigger over an already-configured client.</summary>
    /// <param name="client">The client. Owned and disposed by this trigger.</param>
    /// <param name="ingestor">The ingestor each message is fed to.</param>
    /// <param name="entityName">The queue name, or the topic name when
    /// <see cref="ServiceBusIngestionOptions.SubscriptionName"/> is set.</param>
    /// <param name="options">Tuning for this trigger.</param>
    /// <param name="logger">Optional logger.</param>
    public AzureServiceBusIngestionTrigger(
        ServiceBusClient client,
        IIngestor ingestor,
        string entityName,
        ServiceBusIngestionOptions options,
        ILogger<AzureServiceBusIngestionTrigger>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(ingestor);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _logger = logger ?? NullLogger<AzureServiceBusIngestionTrigger>.Instance;
        _sessionsEnabled = options.SessionsEnabled;
        _entityPath = options.SubscriptionName is null
            ? entityName
            : $"{entityName}/Subscriptions/{options.SubscriptionName}";
        _handler = new ServiceBusMessageIngestionHandler(ingestor, _entityPath, _logger);

        if (options.SessionsEnabled)
        {
            _sessionProcessor = CreateSessionProcessor(client, entityName, options);
            _sessionProcessor.ProcessMessageAsync += _handler.HandleAsync;
            _sessionProcessor.ProcessErrorAsync += OnProcessorErrorAsync;
        }
        else
        {
            _processor = CreateProcessor(client, entityName, options);
            _processor.ProcessMessageAsync += _handler.HandleAsync;
            _processor.ProcessErrorAsync += OnProcessorErrorAsync;
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ServiceBusIngestionLog.ProcessingStarted(_logger, _entityPath, _sessionsEnabled);
        return _sessionProcessor is not null
            ? _sessionProcessor.StartProcessingAsync(cancellationToken)
            : _processor!.StartProcessingAsync(cancellationToken);
    }

    /// <summary>
    /// Stops the receive pump. The SDK waits for in-flight handlers to return, so a message
    /// mid-ingestion is left unsettled rather than settled as a failure.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_sessionProcessor is not null)
            await _sessionProcessor.StopProcessingAsync(cancellationToken).ConfigureAwait(false);
        else
            await _processor!.StopProcessingAsync(cancellationToken).ConfigureAwait(false);

        ServiceBusIngestionLog.ProcessingStopped(_logger, _entityPath);
    }

    /// <summary>Releases the processor, then the client. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Order matters: the processor holds links opened from the client, so closing the
        // client first would fault the processor's own shutdown.
        if (_sessionProcessor is not null)
            await _sessionProcessor.DisposeAsync().ConfigureAwait(false);
        if (_processor is not null)
            await _processor.DisposeAsync().ConfigureAwait(false);

        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnProcessorErrorAsync(ProcessErrorEventArgs args)
    {
        // Transport-level trouble (a lock lost, a link dropped, a receive that failed). The
        // SDK keeps the processor running; this exists so the operator can see it happen.
        ServiceBusIngestionLog.ProcessorError(
            _logger, _entityPath, args.ErrorSource.ToString(), args.Exception);
        return Task.CompletedTask;
    }

    private static ServiceBusProcessor CreateProcessor(
        ServiceBusClient client, string entityName, ServiceBusIngestionOptions options)
    {
        var processorOptions = new ServiceBusProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            // Pinned off: the outcome of ingestion decides the settlement, and auto-complete
            // would settle every message as a success before that outcome existed.
            AutoCompleteMessages = false,
            MaxConcurrentCalls = options.MaxConcurrentCalls,
            PrefetchCount = options.PrefetchCount,
            MaxAutoLockRenewalDuration = options.MaxAutoLockRenewalDuration,
        };

        return options.SubscriptionName is null
            ? client.CreateProcessor(entityName, processorOptions)
            : client.CreateProcessor(entityName, options.SubscriptionName, processorOptions);
    }

    private static ServiceBusSessionProcessor CreateSessionProcessor(
        ServiceBusClient client, string entityName, ServiceBusIngestionOptions options)
    {
        var processorOptions = new ServiceBusSessionProcessorOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            AutoCompleteMessages = false,
            MaxConcurrentSessions = options.MaxConcurrentSessions,
            // Pinned to one, and not configurable. Per-document FIFO is the entire reason to
            // turn sessions on, and a second concurrent call within a session destroys it.
            MaxConcurrentCallsPerSession = 1,
            PrefetchCount = options.PrefetchCount,
            MaxAutoLockRenewalDuration = options.MaxAutoLockRenewalDuration,
        };

        return options.SubscriptionName is null
            ? client.CreateSessionProcessor(entityName, processorOptions)
            : client.CreateSessionProcessor(entityName, options.SubscriptionName, processorOptions);
    }
}

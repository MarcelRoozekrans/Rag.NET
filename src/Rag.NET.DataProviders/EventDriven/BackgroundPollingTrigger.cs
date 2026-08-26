using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.DataProviders.EventDriven;

/// <summary>
/// Background service that re-ingests a provider's files every
/// <see cref="PollingIngestionOptions.PollingInterval"/> via
/// <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> — hash-skip is preserved when
/// an <see cref="IContentHashStore"/> is supplied. A failed cycle logs a warning and the
/// next cycle proceeds (degraded-never-broken); cancellation of the host stopping token
/// exits cleanly. Register via <c>UsePollingIngestion</c>; each registration is an
/// independent poller with its own provider and options.
/// </summary>
public sealed class BackgroundPollingTrigger(
    IRagPipeline pipeline,
    IFileContentProvider provider,
    PollingIngestionOptions options,
    IContentHashStore? hashStore = null,
    ILogger<BackgroundPollingTrigger>? logger = null) : BackgroundService
{
    private readonly ILogger _logger = logger ?? NullLogger<BackgroundPollingTrigger>.Instance;
    private readonly ProviderId _providerId = new(options.ProviderId);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(options.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Clean shutdown: the host cancelled the stopping token mid-cycle or mid-delay.
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await pipeline.IngestFromProviderAsync(
                provider, _providerId, hashStore, cleanupMode: options.CleanupMode,
                cancellationToken: stoppingToken).ConfigureAwait(false);
            DataProvidersLog.PollingCycleCompleted(_logger, options.ProviderId,
                result.IngestedCount, result.SkippedCount, result.FailedCount, result.DeletedCount,
                result.Errors.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // shutdown — handled by ExecuteAsync's clean-exit catch
        }
        catch (Exception ex)
        {
            DataProvidersLog.PollingCycleFailed(_logger, options.ProviderId, ex);
        }
    }
}

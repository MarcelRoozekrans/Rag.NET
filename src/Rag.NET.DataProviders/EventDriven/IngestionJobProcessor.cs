using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders.Logging;
using Rag.NET.Models;

namespace Rag.NET.DataProviders.EventDriven;

/// <summary>
/// Background service that drains the <see cref="IIngestionJobQueue"/>: each job's bytes are
/// wrapped in a stream and handed to <see cref="IIngestor.IngestAsync"/>. A job that fails
/// (failure <c>Result</c> or thrown exception) is logged as a warning and skipped — the
/// processor never crashes (degraded-never-broken). Cancellation of the host stopping token
/// exits the loop cleanly.
/// </summary>
public sealed class IngestionJobProcessor(
    IIngestionJobQueue queue,
    IIngestor ingestor,
    ILogger<IngestionJobProcessor>? logger = null) : BackgroundService
{
    private readonly ILogger _logger = logger ?? NullLogger<IngestionJobProcessor>.Instance;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Clean shutdown: the host cancelled the stopping token while dequeuing or ingesting.
        }
    }

    private async Task ProcessJobAsync(IngestionJob job, CancellationToken stoppingToken)
    {
        var documentId = job.Metadata.DocumentId.Value;
        try
        {
            using var stream = new MemoryStream(job.Content, writable: false);
            var result = await ingestor.IngestAsync(stream, job.Metadata, job.Options,
                progress: null, stoppingToken).ConfigureAwait(false);
            if (!result.IsSuccess)
                DataProvidersLog.IngestionJobFailed(_logger, documentId, result.Error.ToString());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // shutdown — handled by ExecuteAsync's clean-exit catch
        }
        catch (Exception ex)
        {
            DataProvidersLog.IngestionJobThrew(_logger, documentId, ex);
        }
    }
}

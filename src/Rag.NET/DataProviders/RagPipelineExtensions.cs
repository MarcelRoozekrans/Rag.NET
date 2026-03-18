using System.Security.Cryptography;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.DataProviders;

/// <summary>Extension methods for batch ingestion via <see cref="IFileContentProvider"/>.</summary>
public static class RagPipelineExtensions
{
    private enum EntryOutcome { Ingested, Skipped }

    /// <summary>
    /// Ingests all files from <paramref name="provider"/>, skipping unchanged files when
    /// <paramref name="hashStore"/> is supplied. Optionally deletes disappeared documents
    /// when <paramref name="cleanupMode"/> is <see cref="CleanupMode.Full"/>.
    /// </summary>
    public static async Task<ProviderIngestionResult> IngestFromProviderAsync(
        this IRagPipeline pipeline,
        IFileContentProvider provider,
        string providerId,
        IContentHashStore? hashStore = null,
        DocumentMetadata? baseMetadata = null,
        IngestionOptions? options = null,
        CleanupMode cleanupMode = CleanupMode.None,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ingested = 0;
        var skipped = 0;
        var deleted = 0;
        var errors = new List<string>();

        IReadOnlySet<string> knownIds = hashStore is not null && cleanupMode == CleanupMode.Full
            ? await hashStore.GetAllIdsAsync(providerId, cancellationToken).ConfigureAwait(false)
            : (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var entry in provider.GetFilesAsync(cancellationToken).ConfigureAwait(false))
        {
            seenIds.Add(entry.Id);
            var outcome = await ProcessEntryAsync(pipeline, providerId, entry, hashStore, baseMetadata,
                options, progress, errors, cancellationToken).ConfigureAwait(false);
            if (outcome == EntryOutcome.Ingested) ingested++;
            else skipped++;
        }

        if (cleanupMode == CleanupMode.Full && hashStore is not null)
        {
            deleted = await CleanupDisappearedAsync(pipeline, providerId, hashStore, knownIds, seenIds,
                errors, cancellationToken).ConfigureAwait(false);
        }

        return new ProviderIngestionResult(ingested, skipped, deleted, errors);
    }

    private static async Task<EntryOutcome> ProcessEntryAsync(
        IRagPipeline pipeline,
        string providerId,
        FileEntry entry,
        IContentHashStore? hashStore,
        DocumentMetadata? baseMetadata,
        IngestionOptions? options,
        IProgress<IngestionProgress>? progress,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            if (hashStore is not null && entry.ETag is not null)
            {
                var storedETag = await hashStore.GetETagAsync(providerId, entry.Id, cancellationToken).ConfigureAwait(false);
                if (string.Equals(entry.ETag, storedETag, StringComparison.Ordinal))
                    return EntryOutcome.Skipped;
            }

            var rawStream = await entry.OpenContentAsync(cancellationToken).ConfigureAwait(false);
            await using (rawStream.ConfigureAwait(false))
            {
                if (hashStore is null)
                {
                    var metadata = BuildMetadata(entry, baseMetadata);
                    await pipeline.IngestAsync(rawStream, metadata, options, progress, cancellationToken).ConfigureAwait(false);
                    return EntryOutcome.Ingested;
                }

                return await IngestWithHashCheckAsync(pipeline, providerId, entry, hashStore, baseMetadata,
                    options, progress, rawStream, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"{entry.Id}: {ex.Message}");
            return EntryOutcome.Skipped;
        }
    }

    private static async Task<EntryOutcome> IngestWithHashCheckAsync(
        IRagPipeline pipeline,
        string providerId,
        FileEntry entry,
        IContentHashStore hashStore,
        DocumentMetadata? baseMetadata,
        IngestionOptions? options,
        IProgress<IngestionProgress>? progress,
        Stream rawStream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await rawStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var hash = ComputeHash(buffer.GetBuffer(), (int)buffer.Length);

        var storedHash = await hashStore.GetHashAsync(providerId, entry.Id, cancellationToken).ConfigureAwait(false);
        if (string.Equals(hash, storedHash, StringComparison.Ordinal))
        {
            // Only refresh ETag when there's a non-null ETag to store
            if (entry.ETag is not null)
                await hashStore.SetAsync(providerId, entry.Id, entry.ETag, hash, cancellationToken).ConfigureAwait(false);
            return EntryOutcome.Skipped;
        }

        buffer.Position = 0;
        var metadata = BuildMetadata(entry, baseMetadata);
        await pipeline.IngestAsync(buffer, metadata, options, progress, cancellationToken).ConfigureAwait(false);
        await hashStore.SetAsync(providerId, entry.Id, entry.ETag, hash, cancellationToken).ConfigureAwait(false);
        return EntryOutcome.Ingested;
    }

    private static async Task<int> CleanupDisappearedAsync(
        IRagPipeline pipeline,
        string providerId,
        IContentHashStore hashStore,
        IReadOnlySet<string> knownIds,
        HashSet<string> seenIds,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var id in knownIds)
        {
            if (seenIds.Contains(id)) continue;

            try
            {
                await pipeline.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                await hashStore.RemoveAsync(providerId, id, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"delete {id}: {ex.Message}");
            }
        }

        return deleted;
    }

    private static string ComputeHash(byte[] buffer, int length)
    {
        var hashBytes = SHA256.HashData(buffer.AsSpan(0, length));
        return Convert.ToHexString(hashBytes);
    }

    private static DocumentMetadata BuildMetadata(FileEntry entry, DocumentMetadata? baseMetadata)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (baseMetadata?.Tags is not null)
        {
            foreach (var (k, v) in baseMetadata.Tags)
                tags[k] = v;
        }

        if (entry.Metadata is not null)
        {
            foreach (var (k, v) in entry.Metadata)
                tags[k] = v;
        }

        return new DocumentMetadata
        {
            DocumentId = entry.Id,
            FileName = entry.FileName,
            ContentType = baseMetadata?.ContentType,
            Tags = tags,
        };
    }
}

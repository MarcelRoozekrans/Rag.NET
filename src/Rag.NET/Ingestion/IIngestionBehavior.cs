using Rag.NET.Models;

namespace Rag.NET.Ingestion;

public interface IIngestionBehavior
{
    ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx,
        CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next);
}

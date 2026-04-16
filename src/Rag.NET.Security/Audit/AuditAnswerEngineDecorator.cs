using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Security;

/// <summary>
/// Wraps an <see cref="IAnswerEngine"/> and writes an <see cref="AuditAnswerEvent"/> after each answer.
/// Reads the <c>RequestId</c> from <see cref="AuditCorrelationContext"/> (set by <see cref="AuditRetrievalBehavior"/>).
/// Errors from <see cref="IAuditLog"/> are logged, never thrown.
/// </summary>
public sealed partial class AuditAnswerEngineDecorator(
    IAnswerEngine inner,
    IAuditLog auditLog,
    AuditCorrelationContext correlationContext,
    AuditLogOptions options,
    ILogger<AuditAnswerEngineDecorator>? logger = null) : IAnswerEngine
{
    private readonly ILogger<AuditAnswerEngineDecorator> _logger =
        logger ?? NullLogger<AuditAnswerEngineDecorator>.Instance;

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options_param = null,
        CancellationToken cancellationToken = default)
    {
        var response = await inner.AskAsync(query, sources, options_param, cancellationToken).ConfigureAwait(false);
        await LogAnswerAsync(response.Answer, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options_param = null,
        CancellationToken cancellationToken = default)
        => inner.AskStreamingAsync(query, sources, options_param, cancellationToken);

    private async ValueTask LogAnswerAsync(string? answer, CancellationToken ct)
    {
        var requestId = correlationContext.RequestId ?? Guid.NewGuid().ToString("N");
        var ev = new AuditAnswerEvent
        {
            RequestId = requestId,
            Timestamp = DateTimeOffset.UtcNow,
            Answer    = options.LogAnswerText ? answer : null,
        };
        try
        {
            await auditLog.LogAnswerAsync(ev, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogAuditFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AuditAnswerEngineDecorator failed to write audit log entry.")]
    private static partial void LogAuditFailed(ILogger logger, Exception ex);
}

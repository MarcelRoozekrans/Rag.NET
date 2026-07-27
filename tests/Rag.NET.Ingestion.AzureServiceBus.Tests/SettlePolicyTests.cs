using System.Net;
using Rag.NET.Ingestion.AzureServiceBus.Settlement;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// The settle policy is the second pure seam: a total function from outcome to broker action,
/// plus the classification of an ingestion error into an outcome.
/// </summary>
public sealed class SettlePolicyTests
{
    [Theory]
    [InlineData(IngestionOutcome.Succeeded, SettleAction.Complete)]
    [InlineData(IngestionOutcome.TransientFailure, SettleAction.Abandon)]
    [InlineData(IngestionOutcome.PermanentFailure, SettleAction.DeadLetter)]
    [InlineData(IngestionOutcome.ShutdownAborted, SettleAction.Leave)]
    public void For_MapsOutcomeToSettlement(IngestionOutcome outcome, SettleAction expected) =>
        Assert.Equal(expected, SettlePolicy.For(outcome));

    [Fact]
    public void For_UndefinedOutcome_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SettlePolicy.For((IngestionOutcome)42));

    [Fact]
    public void Classify_NoParserFound_IsPermanent() =>
        Assert.Equal(IngestionOutcome.PermanentFailure,
            SettlePolicy.Classify(new RagError.NoParserFound("application/x-nonsense")));

    [Fact]
    public void Classify_ValidationFailed_IsPermanent() =>
        Assert.Equal(IngestionOutcome.PermanentFailure,
            SettlePolicy.Classify(new RagError.ValidationFailed([])));

    [Fact]
    public void Classify_NonSeekableStream_IsPermanent() =>
        Assert.Equal(IngestionOutcome.PermanentFailure, SettlePolicy.Classify(new RagError.NonSeekableStream()));

    [Fact]
    public void Classify_StorageFailed_IsTransient() =>
        Assert.Equal(IngestionOutcome.TransientFailure,
            SettlePolicy.Classify(new RagError.StorageFailed(new IOException("disk"))));

    [Fact]
    public void Classify_TransportFailed_IsTransient() =>
        Assert.Equal(IngestionOutcome.TransientFailure,
            SettlePolicy.Classify(new RagError.TransportFailed(new HttpRequestException("dns"))));

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, IngestionOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, IngestionOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, IngestionOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, IngestionOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.BadRequest, IngestionOutcome.PermanentFailure)]
    [InlineData(HttpStatusCode.Unauthorized, IngestionOutcome.PermanentFailure)]
    public void Classify_HttpFailed_SplitsOnRetryability(HttpStatusCode status, IngestionOutcome expected) =>
        Assert.Equal(expected, SettlePolicy.Classify(new RagError.HttpFailed(status, null)));

    [Fact]
    public void Classify_NullError_Throws() =>
        Assert.Throws<ArgumentNullException>(() => SettlePolicy.Classify(null!));

    [Fact]
    public void DeadLetter_CarriesReasonAndDescription()
    {
        var disposition = MessageDisposition.DeadLetter(DeadLetterReasons.MalformedPayload, "bad bytes");

        Assert.True(disposition.IsDeadLetter);
        Assert.Equal(SettleAction.DeadLetter, disposition.Action);
        Assert.Equal(DeadLetterReasons.MalformedPayload, disposition.Reason);
        Assert.Equal("bad bytes", disposition.Description);
    }

    [Fact]
    public void DeadLetter_BlankReason_Throws() =>
        Assert.Throws<ArgumentException>(() => MessageDisposition.DeadLetter("  ", "why"));
}

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
        Assert.Equal(expected, outcome.ToSettleAction());

    [Fact]
    public void For_UndefinedOutcome_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ((IngestionOutcome)42).ToSettleAction());

    [Fact]
    public void Classify_NoParserFound_IsPermanent() =>
        Assert.Equal(IngestionOutcome.PermanentFailure,
            new RagError.NoParserFound("application/x-nonsense").Classify());

    [Fact]
    public void Classify_ValidationFailed_IsPermanent() =>
        Assert.Equal(IngestionOutcome.PermanentFailure,
            new RagError.ValidationFailed([]).Classify());

    [Fact]
    public void Classify_NonSeekableStream_IsPermanent() =>
        Assert.Equal(IngestionOutcome.PermanentFailure, new RagError.NonSeekableStream().Classify());

    [Fact]
    public void Classify_StorageFailed_IsTransient() =>
        Assert.Equal(IngestionOutcome.TransientFailure,
            new RagError.StorageFailed(new IOException("disk")).Classify());

    /// <summary>
    /// A failed model call is transient: a 429 or an unreadable completion usually succeeds on the
    /// next attempt, which is exactly what a redelivery is for.
    /// </summary>
    /// <remarks>
    /// <c>Classify</c>'s default arm already says "any error added later: assume transient", so
    /// <see cref="RagError.ModelCallFailed"/> was classified correctly the moment it existed (#504).
    /// This pins it anyway: the default makes the answer accidental, and a later reader deciding
    /// how to settle a model failure should find the decision written down rather than infer it
    /// from a fallthrough.
    /// </remarks>
    [Fact]
    public void Classify_ModelCallFailed_IsTransient() =>
        Assert.Equal(IngestionOutcome.TransientFailure,
            new RagError.ModelCallFailed(
                new ModelCallException("rate limited", new InvalidOperationException("429"))).Classify());

    [Fact]
    public void Classify_TransportFailed_IsTransient() =>
        Assert.Equal(IngestionOutcome.TransientFailure,
            new RagError.TransportFailed(new HttpRequestException("dns")).Classify());

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, IngestionOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, IngestionOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, IngestionOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, IngestionOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.BadRequest, IngestionOutcome.PermanentFailure)]
    [InlineData(HttpStatusCode.Unauthorized, IngestionOutcome.PermanentFailure)]
    public void Classify_HttpFailed_SplitsOnRetryability(HttpStatusCode status, IngestionOutcome expected) =>
        Assert.Equal(expected, new RagError.HttpFailed(status, null).Classify());

    /// <remarks>
    /// The receiver is assigned to a typed local first, deliberately. An extension method CAN be
    /// invoked on a null reference, so the guard's behaviour is unchanged by #185's conversion —
    /// but <c>null!.Classify()</c> does not compile: extension lookup cannot bind against an
    /// untyped <c>null</c> literal. Same assertion, different source, and worth writing down
    /// because "the guard still passes" would otherwise be claimed without anyone checking the
    /// call still binds.
    /// </remarks>
    [Fact]
    public void Classify_NullError_Throws()
    {
        RagError error = null!;

        Assert.Throws<ArgumentNullException>(() => error.Classify());
    }

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

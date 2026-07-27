using System.Text;
using Azure.Messaging.ServiceBus;
using Rag.NET.Ingestion.AzureServiceBus.Messaging;
using Rag.NET.Ingestion.AzureServiceBus.Settlement;
using Xunit;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// Translation is a pure function over bytes, so all of it is exercised without a broker.
/// <see cref="ServiceBusReceivedMessage"/> has no public constructor;
/// <see cref="ServiceBusModelFactory"/> is the only way to build one, and that single fact is
/// what makes this layer unit-testable rather than integration-only.
/// </summary>
public sealed class ServiceBusMessageTranslatorTests
{
    private static ServiceBusReceivedMessage Message(string json, string? sessionId = null) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(json),
            sessionId: sessionId);

    [Fact]
    public void Translate_ValidPayload_ProducesJob()
    {
        var message = Message("""
            {
              "documentId": "doc-42",
              "content": "the quick brown fox",
              "metadata": { "source": "crm", "tenant": "acme" }
            }
            """);

        var translation = ServiceBusMessageTranslator.Translate(message);

        Assert.True(translation.IsSuccess);
        Assert.Equal("doc-42", translation.Job.Metadata.DocumentId.Value);
        Assert.Equal("doc-42.txt", translation.Job.Metadata.FileName);
        Assert.Equal("the quick brown fox", Encoding.UTF8.GetString(translation.Job.Content));
        Assert.Equal("crm", translation.Job.Metadata.Tags["source"]);
        Assert.Equal("acme", translation.Job.Metadata.Tags["tenant"]);
        // Left unset on purpose: re-ingest replaces unconditionally, so an at-least-once
        // redelivery needs no per-message options to be safe.
        Assert.Null(translation.Job.Options);
    }

    [Fact]
    public void Translate_ValidPayloadWithoutMetadata_ProducesJobWithNoTags()
    {
        var translation = ServiceBusMessageTranslator.Translate(
            Message("""{ "documentId": "doc-1", "content": "hello" }"""));

        Assert.True(translation.IsSuccess);
        Assert.Empty(translation.Job.Metadata.Tags);
    }

    [Theory]
    [InlineData("""{ "content": "hello" }""")]
    [InlineData("""{ "documentId": "doc-1" }""")]
    [InlineData("""{ "documentId": "", "content": "hello" }""")]
    [InlineData("""{ "documentId": "   ", "content": "hello" }""")]
    [InlineData("""{ "documentId": "doc-1", "content": "" }""")]
    [InlineData("""{ "documentId": 7, "content": "hello" }""")]
    [InlineData("""{ "documentId": "doc-1", "content": "hello", "metadata": { "n": 7 } }""")]
    [InlineData("""{ "documentId": "doc-1", "content": "hello", "metadata": "not-an-object" }""")]
    public void Translate_MissingRequiredField_IsPermanentFailure(string json)
    {
        var translation = ServiceBusMessageTranslator.Translate(Message(json));

        Assert.False(translation.IsSuccess);
        // Dead-letter, NOT abandon: no number of redeliveries turns a payload defect into a
        // valid document, so retrying only burns the delivery budget.
        Assert.Equal(SettleAction.DeadLetter, translation.Failure.Action);
        Assert.Equal(DeadLetterReasons.MissingRequiredField, translation.Failure.Reason);
        Assert.False(string.IsNullOrWhiteSpace(translation.Failure.Description));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{ "documentId": "doc-1", "content": """)]
    [InlineData("42")]
    [InlineData("\"a bare string\"")]
    [InlineData("""[{ "documentId": "doc-1", "content": "hello" }]""")]
    public void Translate_MalformedJson_IsPermanentFailure(string json)
    {
        var translation = ServiceBusMessageTranslator.Translate(Message(json));

        Assert.False(translation.IsSuccess);
        Assert.Equal(SettleAction.DeadLetter, translation.Failure.Action);
        Assert.Equal(DeadLetterReasons.MalformedPayload, translation.Failure.Reason);
    }

    [Theory]
    // Traversal, separators, and a device-ish name: the id survives verbatim as identity,
    // the FILE NAME must not.
    [InlineData("../../etc/passwd", ".._.._etc_passwd.txt")]
    [InlineData("""C:\windows\system32""", "C__windows_system32.txt")]
    [InlineData("a<b>c:d\"e|f?g*h", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("...", "document.txt")]
    [InlineData("///", "document.txt")]
    public void Translate_HostileDocumentId_SanitizesTheFileName(string documentId, string expectedFileName)
    {
        var json = $$"""{ "documentId": {{System.Text.Json.JsonSerializer.Serialize(documentId)}}, "content": "hello" }""";

        var translation = ServiceBusMessageTranslator.Translate(Message(json));

        Assert.True(translation.IsSuccess);
        Assert.Equal(expectedFileName, translation.Job.Metadata.FileName);
        Assert.DoesNotContain("/", translation.Job.Metadata.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", translation.Job.Metadata.FileName, StringComparison.Ordinal);
        // Identity is untouched — only the display/parser-selection file name is sanitized.
        Assert.Equal(documentId, translation.Job.Metadata.DocumentId.Value);
    }

    [Fact]
    public void Translate_SessionIdMatchingDocumentId_ProducesJob()
    {
        var translation = ServiceBusMessageTranslator.Translate(
            Message("""{ "documentId": "doc-9", "content": "hello" }""", sessionId: "doc-9"));

        Assert.True(translation.IsSuccess);
        Assert.Equal("doc-9", translation.Job.Metadata.DocumentId.Value);
    }

    [Fact]
    public void Translate_SessionIdDisagreeingWithDocumentId_IsPermanentFailure()
    {
        var translation = ServiceBusMessageTranslator.Translate(
            Message("""{ "documentId": "doc-9", "content": "hello" }""", sessionId: "tenant-acme"));

        Assert.False(translation.IsSuccess);
        Assert.Equal(DeadLetterReasons.SessionDocumentMismatch, translation.Failure.Reason);
        Assert.Contains("tenant-acme", translation.Failure.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_NullMessage_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            ServiceBusMessageTranslator.Translate((ServiceBusReceivedMessage)null!));
}

using Azure.Core;
using Azure.Core.Pipeline;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;

/// <summary>
/// Writes every response body the client receives to a directory, so a run against the real
/// service leaves the payloads behind as files.
/// </summary>
/// <remarks>
/// <para>
/// This exists because Document Intelligence cannot be recorded through the WireMock proxy the
/// connector cassettes use. Analysis is a long-running operation: the real service answers the
/// POST with an absolute <c>Operation-Location</c> on its own host, so the SDK polls Azure
/// directly and the poll — which carries the entire <c>analyzeResult</c> — never crosses the
/// proxy. A proxy recording would capture the one response that contains nothing and miss the
/// one that contains everything.
/// </para>
/// <para>
/// So the split is deliberate: the mapping envelope stays hand-written, because the parts a
/// recording would supply (path, status, and an <c>Operation-Location</c> pointing back at the
/// mock) are exactly the parts a recording gets wrong, while the response <i>body</i> — the part
/// that encodes what the service actually returns, and the part a hand-written cassette can be
/// wrong about — comes from the real service.
/// </para>
/// </remarks>
/// <param name="directory">Directory to write response bodies into. Created if absent.</param>
public sealed class ResponseCapturePolicy(string directory) : HttpPipelinePolicy
{
    private int _sequence;

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        ProcessNext(message, pipeline);
        Capture(message);
    }

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
        Capture(message);
    }

    /// <summary>
    /// Writes one response body, numbered in arrival order so the analyze answer and each poll
    /// stay distinguishable afterwards.
    /// </summary>
    /// <remarks>
    /// Restores the stream position rather than consuming it: the SDK deserialises the same
    /// stream once this policy returns, so a capture that left the position at the end would
    /// break the call it was observing — and the call this exists to observe bills a page and
    /// cannot be cheaply repeated. <c>Capture_LeavesTheResponseReadableByTheCaller</c> is the
    /// test that holds it.
    /// </remarks>
    /// <param name="message">The message whose response to write.</param>
    private void Capture(HttpMessage message)
    {
        if (!message.HasResponse || message.Response.ContentStream is not { CanSeek: true } content)
        {
            return;
        }

        Directory.CreateDirectory(directory);

        var position = content.Position;
        content.Position = 0;
        using var reader = new StreamReader(content, leaveOpen: true);
        var body = reader.ReadToEnd();
        content.Position = position;

        File.WriteAllText(
            Path.Combine(directory, $"{++_sequence:00}-{message.Response.Status}.json"),
            body);
    }
}

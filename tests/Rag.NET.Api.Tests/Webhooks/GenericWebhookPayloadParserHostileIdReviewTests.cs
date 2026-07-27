using System.Text.Json;
using Rag.NET.Api.Webhooks;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Api.Tests.Webhooks;

/// <summary>
/// Review-added coverage for hostile <c>documentId</c> values reaching
/// <see cref="GenericWebhookPayloadParser"/> from an untrusted HTTP payload. Exercises the
/// parser directly rather than through the endpoint so the input can be arbitrary text without
/// hand-escaping JSON.
/// </summary>
public sealed class GenericWebhookPayloadParserHostileIdReviewTests
{
    private static IngestionJob ParseSingle(string documentId)
    {
        using var document = JsonDocument.Parse(BuildPayload(documentId));
        var parser = new GenericWebhookPayloadParser();
        Assert.True(parser.TryParse(document.RootElement, out var jobs));
        return Assert.Single(jobs!);
    }

    private static bool TryParse(string documentId, out IReadOnlyList<IngestionJob>? jobs)
    {
        using var document = JsonDocument.Parse(BuildPayload(documentId));
        return new GenericWebhookPayloadParser().TryParse(document.RootElement, out jobs);
    }

    private static string BuildPayload(string documentId)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("documentId", documentId);
            writer.WriteString("content", "hello");
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Theory]
    // Path traversal, absolute paths, drive letters, UNC.
    [InlineData("../../etc/passwd", ".._.._etc_passwd.txt")]
    [InlineData("..\\..\\windows\\win.ini", ".._.._windows_win.ini.txt")]
    [InlineData("/etc/passwd", "_etc_passwd.txt")]
    [InlineData("C:\\Windows\\System32\\config\\SAM", "C__Windows_System32_config_SAM.txt")]
    [InlineData("\\\\server\\share\\secret", "__server_share_secret.txt")]
    // Doubled-up traversal: each separator becomes an underscore, dots are kept because ".."
    // without a separator either side is an ordinary name fragment.
    [InlineData("....//....//etc", "....__....__etc.txt")]
    // Null byte and other C0 control characters.
    [InlineData("a\u0000b", "a_b.txt")]
    [InlineData("a\u001fb", "a_b.txt")]
    // Trailing dots and spaces (invalid on Windows, re-exposed by each other).
    [InlineData("doc   ", "doc.txt")]
    [InlineData("doc...", "doc.txt")]
    [InlineData("doc. . .", "doc.txt")]
    // Collapses to nothing -> shared fallback stem, never empty, never a bare extension.
    [InlineData("///", "document.txt")]
    [InlineData("..", "document.txt")]
    [InlineData("<<>>", "document.txt")]
    // Other shell/URL metacharacters that are not in the invalid set but are not separators.
    [InlineData("a;rm -rf /", "a;rm -rf _.txt")]
    public void HostileDocumentId_ProducesSafeFileName(string documentId, string expectedFileName)
    {
        var job = ParseSingle(documentId);

        Assert.Equal(expectedFileName, job.Metadata.FileName);
        Assert.DoesNotContain('/', job.Metadata.FileName);
        Assert.DoesNotContain('\\', job.Metadata.FileName);
        Assert.DoesNotContain('\0', job.Metadata.FileName);
        // Identity is preserved verbatim.
        Assert.Equal(documentId, job.Metadata.DocumentId.Value);
    }

    [Fact]
    public void OverLongDocumentId_IsCappedAt128CharacterStem()
    {
        var job = ParseSingle(new string('a', 300));

        Assert.Equal(new string('a', 128) + ".txt", job.Metadata.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void EmptyOrWhitespaceDocumentId_IsRejectedBeforeSanitization(string documentId)
    {
        Assert.False(TryParse(documentId, out var jobs));
        Assert.Null(jobs);
    }

    /// <summary>
    /// Reserved Windows device names are documented as deliberately NOT handled by
    /// <c>FileNameSanitizer</c>, on the grounds that <c>FileName</c> is display and
    /// parser-selection metadata rather than a path. This test pins that documented behaviour so
    /// the gap is visible rather than assumed.
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("NUL")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void ReservedDeviceName_IsDeliberatelyNotNeutralised(string documentId)
    {
        var job = ParseSingle(documentId);

        Assert.Equal(documentId + ".txt", job.Metadata.FileName);
    }
}

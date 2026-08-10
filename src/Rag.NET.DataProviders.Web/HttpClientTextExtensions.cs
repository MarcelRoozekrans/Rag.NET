using System.Text;

namespace Rag.NET.DataProviders.Web;

/// <summary>
/// <see cref="HttpClient"/> text reads that tolerate a declared charset this runtime cannot
/// resolve.
/// <para>
/// <b>Why this exists rather than <c>HttpClient.GetStringAsync</c>.</b> That method reads the
/// charset out of <c>Content-Type</c> and hands it to <see cref="Encoding.GetEncoding(string)"/>.
/// .NET Core registers only a small set of encodings, so a perfectly valid header like
/// <c>text/plain;charset=ISO-8859-2</c> throws <see cref="InvalidOperationException"/> — *"The
/// character set provided in ContentType is invalid"* — wrapping an
/// <see cref="ArgumentException"/>. Neither is an <see cref="HttpRequestException"/>, so the
/// callers' <c>catch (HttpRequestException)</c> never saw it and the exception ended the whole
/// crawl or feed read (issue #123, reported against a live site).
/// </para>
/// <para>
/// <b>This deliberately does not call <see cref="Encoding.RegisterProvider"/>.</b> Registering
/// <c>CodePagesEncodingProvider</c> would make ISO-8859-2 decode exactly, but it mutates
/// process-global state from inside a library, changing behaviour for every other component in
/// the host. A host that wants exact decoding of legacy charsets can register it itself, and this
/// reader picks it up automatically, because <see cref="Encoding.GetEncoding(string)"/> consults
/// registered providers.
/// </para>
/// <para>
/// The fallback is UTF-8, matching HTTP's own default for text content. For a legacy single-byte
/// charset that means high bytes decode to replacement characters rather than the intended
/// letters — the page is degraded, not lost, and a crawler that keeps going on a mis-declared
/// page is worth more than one that stops. A byte-order mark, when present, still wins over both
/// the header and the fallback.
/// </para>
/// </summary>
internal static class HttpClientTextExtensions
{
    /// <summary>
    /// Fetches <paramref name="url"/> and decodes the body as text, falling back to UTF-8 when
    /// the declared charset cannot be resolved.
    /// <para>
    /// <b>Named to be unmistakable, not convenient.</b> An extension method called
    /// <c>GetStringAsync</c> would be silently shadowed — C# binds the instance method first — so
    /// a caller reaching for it out of habit would get <see cref="HttpClient"/>'s own version
    /// back, and with it the crash this exists to prevent. The long name is the point.
    /// </para>
    /// </summary>
    /// <exception cref="HttpRequestException">
    /// The request failed or returned a non-success status — the same exception, from the same
    /// cause, that <c>GetStringAsync</c> raised, so existing handlers keep working unchanged.
    /// </exception>
    public static async Task<string> GetStringWithCharsetFallbackAsync(
        this HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        _ = response.EnsureSuccessStatusCode();

        var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            // detectEncodingFromByteOrderMarks: a BOM is authoritative and overrides both the
            // header's claim and the fallback, which is what GetStringAsync does too.
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The declared charset when this runtime can resolve it, UTF-8 otherwise.
    /// </summary>
    /// <param name="charSet">
    /// The <c>charset</c> parameter from <c>Content-Type</c>, or <see langword="null"/> when the
    /// server declared none.
    /// </param>
    private static Encoding ResolveEncoding(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return Encoding.UTF8;
        }

        // Servers quote it — charset="utf-8" is legal and Encoding.GetEncoding rejects the quotes.
        var trimmed = charSet.Trim().Trim('"');

        try
        {
            return Encoding.GetEncoding(trimmed);
        }
        catch (ArgumentException)
        {
            // Unregistered ("ISO-8859-2") or nonsense ("utf8mb4"). Both are the server's problem
            // and neither is worth ending a crawl over.
            return Encoding.UTF8;
        }
    }
}

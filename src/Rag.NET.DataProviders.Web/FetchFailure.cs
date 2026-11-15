using System.Net;
using Rag.NET.Models;

namespace Rag.NET.DataProviders.Web;

/// <summary>Which fetch failures a web provider skips rather than ending its walk on.</summary>
internal static class FetchFailure
{
    /// <summary>
    /// Whether <paramref name="exception"/> is one page's fetch failing: an HTTP error status, a
    /// response that breaks off, or an <see cref="HttpClient"/> timeout.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpRequestException"/> alone missed two of these. A body that ends early throws
    /// <see cref="HttpIOException"/>, an <see cref="IOException"/>, and a timeout throws
    /// <see cref="TaskCanceledException"/>; either ended a live 675-page crawl on one page. A
    /// connection reset during the body read throws a plain <see cref="IOException"/> on .NET 10,
    /// not an <see cref="HttpIOException"/>, so any <see cref="IOException"/> counts. The callers
    /// wrap only the fetch and its body read, so no other I/O reaches this. The caller's own
    /// cancellation is excluded, so cancelling still stops the walk.
    /// </remarks>
    public static bool IsSkippable(Exception exception, CancellationToken callerToken) =>
        exception is HttpRequestException or IOException
        || (exception is TaskCanceledException && !callerToken.IsCancellationRequested);

    /// <summary>
    /// Whether the site answered that <paramref name="exception"/>'s resource is not there: a 4xx
    /// other than 429, or a 3xx left after <see cref="HttpClient"/> stops following redirects.
    /// Every other skippable failure may pass, and is reported with <see cref="ToError"/>.
    /// </summary>
    /// <remarks>
    /// Only the site's own answer counts: a proxy that refuses the tunnel also carries a status
    /// (407, say) but sets <see cref="HttpRequestException.HttpRequestError"/>, and the site was
    /// never reached. 429 is a 4xx, but it says "slow down", not "there is nothing here".
    /// </remarks>
    public static bool IsAbsent(Exception exception) =>
        SiteStatus(exception) is { } status && status != HttpStatusCode.TooManyRequests && (int)status < 500;

    /// <summary>
    /// The error to report for a skippable failure of <paramref name="url"/> that is not
    /// <see cref="IsAbsent"/>: <see cref="RagError.HttpFailed"/> when the site answered with a
    /// status, <see cref="RagError.TransportFailed"/> otherwise. <paramref name="consequence"/>
    /// ends the message.
    /// </summary>
    public static RagError ToError(Exception exception, string url, string consequence) =>
        SiteStatus(exception) is { } status
            ? new RagError.HttpFailed(status, $"{url} returned {(int)status}. {consequence}")
            : new RagError.TransportFailed(new HttpRequestException($"{url} could not be fetched. {consequence}", exception));

    private static HttpStatusCode? SiteStatus(Exception exception) =>
        exception is HttpRequestException { StatusCode: { } status, HttpRequestError: HttpRequestError.Unknown }
            ? status
            : null;
}

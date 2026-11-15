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
}

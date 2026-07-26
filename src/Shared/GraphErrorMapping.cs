using System.Net;
using Azure.Identity;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.DataProviders.Graph;

/// <summary>
/// Shared classification of the exceptions a Microsoft Graph call can raise, used by every
/// Graph-backed connector (Exchange, SharePoint, OneDrive, Microsoft Teams).
/// <para>
/// This file lives in <c>src/Shared</c> and is <b>linked</b> into each connector project
/// rather than published as a library. Each connector is its own assembly and NuGet package,
/// and the one shared package they shed in common — <c>Rag.NET.DataProviders</c> —
/// deliberately references neither Microsoft.Graph, Kiota, nor Azure.Identity, so it cannot
/// name <see cref="ODataError"/>, <see cref="ApiException"/>, or
/// <see cref="AuthenticationFailedException"/>. Linking keeps one definition without adding a
/// package dependency anywhere.
/// </para>
/// </summary>
internal static class GraphErrorMapping
{
    /// <summary>
    /// Whether <paramref name="ex"/> is a Graph failure a connector converts into a
    /// <c>Result</c> failure rather than letting it escape.
    /// <para>
    /// The cancellation test comes first and wins: an <see cref="HttpClient"/> timeout
    /// surfaces as a <see cref="TaskCanceledException"/>, which derives from
    /// <see cref="OperationCanceledException"/>, so only the caller's token separates a
    /// timeout (a transport failure, mapped) from a caller cancellation (which must
    /// propagate untouched).
    /// </para>
    /// </summary>
    public static bool IsMappable(Exception ex, CancellationToken cancellationToken)
        => !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        && ex is ApiException or HttpRequestException or TaskCanceledException
              or AuthenticationFailedException;

    /// <summary>
    /// Classifies an exception for which <see cref="IsMappable"/> returned
    /// <see langword="true"/>.
    /// <para>
    /// Kiota leaves <see cref="ApiException.ResponseStatusCode"/> at <c>0</c> when no HTTP
    /// response was received at all, and <c>(HttpStatusCode)0</c> is not a valid status —
    /// that case is a <see cref="RagError.TransportFailed"/>, not a
    /// <see cref="RagError.HttpFailed"/> carrying an out-of-range status.
    /// </para>
    /// </summary>
    public static RagError Map(Exception ex) => ex switch
    {
        ApiException { ResponseStatusCode: 0 } => new RagError.TransportFailed(ex),
        ODataError odata => new RagError.HttpFailed(
            (HttpStatusCode)odata.ResponseStatusCode, odata.Error?.Message ?? odata.Message),
        ApiException api => new RagError.HttpFailed(
            (HttpStatusCode)api.ResponseStatusCode, api.Message),
        // HttpRequestException, TaskCanceledException (an HttpClient timeout, since caller
        // cancellation was filtered out by IsMappable), AuthenticationFailedException.
        _ => new RagError.TransportFailed(ex),
    };

    /// <summary>
    /// Whether <paramref name="ex"/> reports that a stored <c>deltaLink</c> is no longer
    /// usable — the token has aged out (<c>resyncRequired</c>) or the item it was scoped to
    /// is gone (<c>itemNotFound</c>). Both mean "start over with a full traversal" and are
    /// therefore <b>not</b> failures. Every other <see cref="ODataError"/>, including
    /// <c>accessDenied</c>, is a real failure and must not silently fall back.
    /// </summary>
    public static bool IsStaleDeltaToken(ODataError ex)
        => string.Equals(ex.Error?.Code, "resyncRequired", StringComparison.Ordinal)
        || string.Equals(ex.Error?.Code, "itemNotFound", StringComparison.Ordinal);
}

using Microsoft.Extensions.AI;
using Rag.NET.Models.Options;

namespace Rag.NET.Remote;

/// <summary>
/// The checks a remote <c>IRagPipeline</c> client runs before it sends options to its server, so
/// that a call that cannot be sent, or that the in-process <c>RagPipeline</c> would reject, fails
/// the same way, before anything goes over the wire (#944).
/// </summary>
/// <remarks>
/// This file lives in <c>src/Shared</c> and is linked into the HTTP and gRPC client projects, as
/// <c>GraphErrorMapping.cs</c> is, so no package gains a public type. It reads
/// <c>RagOptions.AssignedRetrievalFields</c>, which is internal to Rag.NET.Abstractions; each
/// client is listed in that project's <c>InternalsVisibleTo</c> for this file.
/// </remarks>
internal static class RemoteOptionsGuard
{
    /// <summary>
    /// Throws when <paramref name="options"/> cannot be sent as it is.
    /// </summary>
    /// <param name="options">The caller's options.</param>
    /// <param name="client">The client's type name, for the messages.</param>
    /// <returns>The short-form retrieval fields the caller assigned, by name, to send.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="RagOptions.Retrieval"/> is set together with a short-form retrieval field, which
    /// the in-process pipeline rejects the same way.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The options hold something only an in-process pipeline can use: a
    /// <see cref="RetrievalOptions.Filter"/>, or a conversation turn that is not plain text.
    /// </exception>
    internal static IReadOnlyList<string> CheckSendable(RagOptions options, string client)
    {
        var assigned = options.AssignedRetrievalFields;
        if (options.Retrieval is not null && assigned.Count > 0)
            throw new ArgumentException(
                $"RagOptions.Retrieval was set together with {string.Join(", ", assigned)}. "
                + "Retrieval replaces those fields; set their values on RagOptions.Retrieval instead.",
                nameof(options));

        if (options.Retrieval is { } retrieval)
            CheckSendable(retrieval, client);

        if (options.ConversationHistory is { } history
            && history.SelectMany(m => m.Contents).Any(c => c is not TextContent))
        {
            throw new NotSupportedException(
                $"{client} can send only text in RagOptions.ConversationHistory; a turn holds "
                + "other content, such as an image or a function call. Call an in-process RagPipeline.");
        }

        return assigned;
    }

    /// <summary>Throws when <paramref name="options"/> cannot be sent as it is.</summary>
    /// <param name="options">The caller's retrieval options.</param>
    /// <param name="client">The client's type name, for the message.</param>
    /// <exception cref="NotSupportedException"><see cref="RetrievalOptions.Filter"/> is set.</exception>
    internal static void CheckSendable(RetrievalOptions options, string client)
    {
        if (options.Filter is not null)
            throw new NotSupportedException(
                $"{client} cannot send RetrievalOptions.Filter: it is a specification object, code that "
                + "runs in-process, not data. Use MetadataFilter, or call an in-process RagPipeline.");
    }
}

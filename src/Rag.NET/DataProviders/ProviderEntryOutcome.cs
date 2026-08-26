using Rag.NET.Models;

namespace Rag.NET.DataProviders;

/// <summary>What became of one entry during a <c>IngestFromProviderAsync</c> run.</summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> <see cref="FileEntry"/>, which #395 proposed. A
/// <see cref="FileEntry"/> carries <c>Func&lt;CancellationToken, Task&lt;Stream&gt;&gt;
/// OpenContentAsync</c> — a live delegate that opens the entry's content — and a completed run's
/// report is the wrong place to hand that back. It would keep every closure alive after the run,
/// along with whatever each captured, and it would invite callers to open content from a source
/// that may already be gone.
/// </para>
/// <para>
/// A report needs to say <i>which</i> entry, not offer to fetch it again. That is also what makes
/// <c>Deleted</c> expressible at all: a deleted document is by definition one the provider no longer
/// lists, so no <see cref="FileEntry"/> for it exists — only the id the hash store recorded.
/// </para>
/// </remarks>
/// <param name="Id">The provider's id for the entry.</param>
/// <param name="FileName">
/// The entry's file name, or <see langword="null"/> for a deleted document — nothing listed it this
/// run, so only its id survives.
/// </param>
/// <param name="ETag">The entry's ETag when the provider supplied one.</param>
public sealed record ProviderEntryOutcome(EntryId Id, string? FileName = null, string? ETag = null);

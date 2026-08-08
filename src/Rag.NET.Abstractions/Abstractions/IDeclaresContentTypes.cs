namespace Rag.NET.Abstractions;

/// <summary>
/// Optional companion to <see cref="IDocumentParser"/>. A parser that can honestly enumerate every
/// content type its <see cref="IDocumentParser.CanParse"/> accepts implements this alongside it, so
/// <c>RagBuilder.AddParser&lt;TParser&gt;()</c> can declare one <see cref="ParserClaim"/> per type
/// automatically instead of leaving that registration invisible to the guard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why declaring rather than discovering is safe here, when it is not for <c>CanParse</c>
/// itself.</b> <see cref="ParserClaim"/>'s own remarks explain what this does not reopen: probing
/// <c>CanParse</c> against a guessed list of content types would be "a worse mechanism than an
/// undetected collision", because the guesser has no way to know it guessed completely. A parser
/// stating its own accepted set is not a guess — it is the parser's own answer, read directly rather
/// than inferred from the outside.
/// </para>
/// <para>
/// <b>Opt-in and additive, deliberately.</b> <see cref="IDocumentParser"/> itself does not change.
/// A parser whose accepted set cannot be enumerated honestly — a wildcard, a set computed from
/// runtime configuration — simply does not implement this, and keeps exactly today's behaviour:
/// undetected by the claim guard, the same as every parser reached through
/// <c>AddParser&lt;TParser&gt;()</c> before this interface existed. A third-party parser that
/// implements only <see cref="IDocumentParser"/> is unaffected either way — nothing about it needs
/// to change for it to keep working.
/// </para>
/// <para>
/// <b>Static, not instance.</b> The content types a parser accepts do not depend on how it was
/// constructed, and <c>AddParser&lt;TParser&gt;()</c> only ever registers a
/// <see cref="Microsoft.Extensions.DependencyInjection.ServiceDescriptor"/> — it never resolves an
/// instance, and several parsers this repository ships (<c>PdfDocumentParser</c>,
/// <c>AudioDocumentParser</c>, the Vision parsers) take constructor dependencies —
/// <c>IChatClient</c>, options, a logger — that are not necessarily available yet at the point
/// <c>AddParser</c> runs. A static member is the only shape that can be read without one.
/// </para>
/// <para>
/// <b>A wrong entry here is worse than an absent one.</b> Everything <see cref="ContentTypes"/>
/// lists must be a type <see cref="IDocumentParser.CanParse"/> actually accepts — declaring a type
/// <c>CanParse</c> would reject turns the guard into something that asserts a false thing about the
/// parser's behaviour, which is strictly worse than the guard staying silent about it. Keeping the
/// two together is what a coverage test over every implementer exists to hold, in both directions.
/// </para>
/// </remarks>
public interface IDeclaresContentTypes
{
    /// <summary>
    /// Every content type the implementing <see cref="IDocumentParser"/>'s <c>CanParse</c> accepts.
    /// Each entry becomes one <see cref="ParserClaim"/> when the parser is registered through
    /// <c>RagBuilder.AddParser&lt;TParser&gt;()</c>.
    /// </summary>
    static abstract IReadOnlyCollection<string> ContentTypes { get; }
}

namespace Rag.NET.Abstractions;

/// <summary>
/// A content type that a registration call declares one of its parsers will claim, registered as
/// a singleton alongside the parser itself so <c>AddRagNet</c> can detect two parsers claiming the
/// same type before anything is resolved.
/// </summary>
/// <param name="ContentType">The content type the parser's <c>CanParse</c> accepts.</param>
/// <param name="ParserTypeName">
/// The claiming parser's full type name. Full rather than short is load-bearing, not cosmetic.
/// The validator treats one type name as one claimant, so that registering the same package twice
/// is not a conflict; short names make two <i>distinct</i> parsers that happen to share a name
/// collapse into a single claimant, and the check stops firing on exactly the collision it exists
/// for. <c>ParserClaimValidationTests.TwoParsersSharingAShortName_StillConflict</c> is what holds
/// this line to <c>FullName</c>: it registers two parsers both called <c>SharedNameParser</c> from
/// different namespaces, both claiming one content type, and mutating this line to
/// <c>typeof(TParser).Name</c> turns it red with "no exception was thrown".
/// <para>
/// History, because the reason that test exists is not obvious. The rule was originally pinned by
/// nothing but the repository's own accident: both colliding types were literally called
/// <c>EmailDocumentParser</c>, and the same mutation turned four conflict tests red. Phase 3.11
/// then renamed one of them to <c>EmailTemplateDocumentParser</c> and, in doing so, abolished the
/// coverage — the same mutation afterwards reddened one test, for an unrelated reason, and no test
/// watched the rule the rename had just made un-hit. A same-named pair in a third-party package is
/// not something this repository can rename away, so the rule outlived the pair that demonstrated
/// it and now has a test of its own.
/// </para>
/// </param>
/// <param name="RegistrationMethod">
/// The call a user would recognise from their own composition root — <c>AddEmailParser()</c>,
/// <c>UseEmailChunking()</c> — rather than the internal registration that performed it.
/// </param>
/// <param name="ParserOptOut">
/// How to keep <see cref="RegistrationMethod"/> while dropping only the parser it registers, or
/// <see langword="null"/> when that call registers nothing but the parser and removing the call is
/// the only option. Declared per registration site rather than composed by the validator, so the
/// conflict message can offer a real escape hatch without <c>AddRagNet</c> knowing anything about
/// the packages that collide. Some calls bundle a parser with a chunking strategy, and telling a
/// user to "register only one of them" when the strategy they want is only reachable through the
/// colliding call is advice they cannot take.
/// </param>
/// <param name="ReplacesParserTypeName">
/// The full type name of the parser this claim's registration deliberately overrode via
/// <c>RagBuilder.AddParser&lt;TParser&gt;(replaces:)</c>, or <see langword="null"/> when this claim
/// did not replace one. <c>AddParser&lt;TParser&gt;(replaces:)</c> itself removes the replaced
/// parser's descriptor and claim outright rather than leaving two claimants for the validator to
/// referee — this property exists so the claim that took its place can still record what it
/// overrode, for anyone reading the registration back rather than for the validator itself.
/// </param>
/// <remarks>
/// <para>
/// The claim is declared rather than discovered because neither route to discovering it works at
/// registration time. Calling <c>CanParse</c> needs live instances, which means building a service
/// provider while the collection is still being populated. And
/// <c>ServiceDescriptor.ImplementationType</c> is <see langword="null"/> for every registration
/// that collides here — they all use factory lambdas, so only <c>ImplementationFactory</c> is set.
/// </para>
/// <para>
/// The limit is that this catches only registrations that <i>declare</i> a claim — which is not
/// the same boundary as "first-party", and saying so understated it. The two parsers
/// <c>AddRagNETServices()</c> auto-registers, <c>TextDocumentParser</c> and
/// <c>MarkdownDocumentParser</c>, declared nothing when the guard shipped, so registering a parser
/// that claimed <c>text/plain</c> left one declared claimant and the guard stayed silent while
/// selection resolved <c>text/plain</c> to the built-in and the user's parser never ran. They
/// declare their claims now, from <c>AddRagNet</c> itself, because a source generator writes their
/// registrations and cannot host one.
/// </para>
/// <para>
/// What genuinely goes undetected is a parser registered through <c>AddParser&lt;T&gt;()</c>,
/// which declares nothing. <c>CanParse</c> is a predicate rather than an enumeration, so nothing
/// can discover what an arbitrary parser accepts without probing it against a guessed list of
/// content types — a worse mechanism than an undetected collision.
/// </para>
/// </remarks>
public sealed record ParserClaim(
    string ContentType,
    string ParserTypeName,
    string RegistrationMethod,
    string? ParserOptOut = null,
    string? ReplacesParserTypeName = null)
{
    /// <summary>
    /// Builds a claim for <typeparamref name="TParser"/>, taking the parser type name from the
    /// type itself so a rename cannot leave the claim naming a type that no longer exists.
    /// </summary>
    /// <param name="replaces">
    /// The parser type this registration deliberately overrode, if any. Recorded on
    /// <see cref="ReplacesParserTypeName"/> as a full type name — see that property's remarks.
    /// </param>
    /// <param name="replacesTypeName">
    /// The full type name of the parser this registration deliberately overrode, for callers that
    /// cannot reference <paramref name="replaces"/> as a <see cref="Type"/> — a chunking-templates
    /// package declaring an override against a parser that lives in an optional package it must not
    /// take a compile-time dependency on, for instance. Ignored when <paramref name="replaces"/> is
    /// set; the two exist for the same property because one caller has the <see cref="Type"/> and
    /// the other only ever has its name.
    /// </param>
    public static ParserClaim For<TParser>(
        string contentType,
        string registrationMethod,
        string? parserOptOut = null,
        Type? replaces = null,
        string? replacesTypeName = null)
        where TParser : IDocumentParser =>
        new(
            contentType,
            typeof(TParser).FullName ?? typeof(TParser).Name,
            registrationMethod,
            parserOptOut,
            replaces?.FullName ?? replaces?.Name ?? replacesTypeName);
}

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
/// for. Verified rather than reasoned: when the two colliding types were both called
/// <c>EmailDocumentParser</c>, mutating this line to <c>typeof(TParser).Name</c> turned four
/// conflict tests from passing to "no exception was thrown". Phase 3.11 went on to rename one of
/// them to <c>EmailTemplateDocumentParser</c>, which removes the hazard for that particular pair
/// and for no other — the next same-named pair, in a third-party package this repository cannot
/// rename, would silently disable the guard again.
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
/// <remarks>
/// <para>
/// The claim is declared rather than discovered because neither route to discovering it works at
/// registration time. Calling <c>CanParse</c> needs live instances, which means building a service
/// provider while the collection is still being populated. And
/// <c>ServiceDescriptor.ImplementationType</c> is <see langword="null"/> for every registration
/// that collides here — they all use factory lambdas, so only <c>ImplementationFactory</c> is set.
/// </para>
/// <para>
/// The limit is that this catches only registrations that declare a claim. A third-party parser
/// registered through <c>AddParser&lt;T&gt;()</c> declares nothing and goes undetected.
/// <c>CanParse</c> is a predicate rather than an enumeration, so nothing can discover what an
/// arbitrary parser accepts without probing it against a guessed list of content types — a worse
/// mechanism than an undetected third-party collision.
/// </para>
/// </remarks>
public sealed record ParserClaim(
    string ContentType,
    string ParserTypeName,
    string RegistrationMethod,
    string? ParserOptOut = null)
{
    /// <summary>
    /// Builds a claim for <typeparamref name="TParser"/>, taking the parser type name from the
    /// type itself so a rename cannot leave the claim naming a type that no longer exists.
    /// </summary>
    public static ParserClaim For<TParser>(
        string contentType,
        string registrationMethod,
        string? parserOptOut = null)
        where TParser : IDocumentParser =>
        new(contentType, typeof(TParser).FullName ?? typeof(TParser).Name, registrationMethod, parserOptOut);
}

namespace Rag.NET.Abstractions;

/// <summary>
/// Supplies new <see cref="Guid"/> values, so code that mints identifiers can be tested (#380).
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than a <see cref="TimeProvider"/>-style abstract class, because that is what
/// every abstraction this library authors is — <c>IVectorStore</c>, <c>IBm25Index</c>,
/// <c>ICostLedger</c>, <c>IAuditLog</c>. <c>TimeProvider</c> is an abstract class because Microsoft
/// ships it that way, which is a reason to use it, not a house pattern to copy. It also substitutes
/// with the mocking library the tests here already use.
/// </para>
/// <para>
/// <b>It returns a <see cref="Guid"/>, not a string.</b> Formatting is the caller's business, and
/// the call sites do not agree on one: some use <c>"N"</c>, others the default form. Returning a
/// string would bake one of them into the abstraction and throw the type away.
/// </para>
/// <para>
/// <b>Not every <c>Guid.NewGuid()</c> belongs behind this.</b> It is for identifiers that reach
/// observable output — a request id in an audit record, a document id handed back to a caller, a
/// tool-call id in a chat message — where a test needs to say what the value will be. Temporary
/// file names, prompt delimiters and internal container keys are left alone: injecting those adds
/// ceremony and buys no test.
/// </para>
/// </remarks>
public interface IGuidProvider
{
    /// <summary>Returns a new identifier.</summary>
    Guid NewGuid();
}

namespace Rag.NET.Abstractions;

/// <summary>
/// Optional capability interface for <see cref="IVectorStore"/> decorators that want
/// diagnostics to name the store they wrap rather than themselves — the same probe pattern
/// as <see cref="IScoreScaleAware"/>. Callers discover the capability with an
/// <c>is IVectorStoreDecorator</c> check on the resolved store and treat its absence as
/// "not decorated": <c>GetType().Name</c> is then the store's own name.
/// </summary>
/// <remarks>
/// Deliberately exposes the inner store's <see cref="Type"/> and not the store itself, so no
/// caller can reach around whatever behaviour the decorator adds. Introduced for the package
/// decomposition: persistent conversation memory names the store responsible for an opaque
/// score scale, and <c>ResilientVectorStore</c> (in <c>Rag.NET.Resilience</c>) is the
/// decorator it must see past — a direct type check would couple <c>Rag.NET.Memory</c> to the
/// resilience package. Decoration never stacks in the shipped registrations, so consumers may
/// treat a single unwrap as sufficient.
/// </remarks>
public interface IVectorStoreDecorator
{
    /// <summary>
    /// The runtime <see cref="Type"/> of the decorated store, for diagnostics that need to
    /// name the store rather than the decorator.
    /// </summary>
    Type InnerStoreType { get; }
}

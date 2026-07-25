using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Deferred view over the registered <see cref="IDocumentParser"/>s, resolved at iteration
/// time. The email parsers depend on the full parser collection (attachment dispatch) while
/// being part of that collection themselves; constructor-injecting
/// <c>IEnumerable&lt;IDocumentParser&gt;</c> directly would be a circular dependency, so
/// <see cref="EmailParserBuilderExtensions.AddEmailParser{TBuilder}"/> injects this instead.
/// Iteration only happens during attachment parsing, long after construction.
/// </summary>
internal sealed class LazyDocumentParsers(IServiceProvider provider) : IEnumerable<IDocumentParser>
{
#pragma warning disable HLQ006 // interface implementation must return IEnumerator<T>; enumeration is rare (attachment dispatch only)
    public IEnumerator<IDocumentParser> GetEnumerator() =>
        provider.GetServices<IDocumentParser>().GetEnumerator();
#pragma warning restore HLQ006

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

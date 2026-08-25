namespace Rag.NET.DependencyInjection;

/// <summary>
/// The service types <c>AddRagNetShared</c> declared, which every named pipeline forwards to the
/// root provider instead of registering for itself.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton on the root collection so that <c>AddRagNet(name, …)</c> can read it
/// regardless of call order — a named block may be declared before the shared block.
/// </para>
/// <para>
/// A set rather than a list, because declaring the same service type shared twice must forward once:
/// duplicate descriptors in a child collection make <c>IEnumerable&lt;T&gt;</c> resolution return it
/// twice, which is the kind of silent doubling that does not fail until something counts.
/// </para>
/// </remarks>
internal sealed class SharedServiceTypes
{
    private readonly List<Type> _types = [];
    private readonly HashSet<Type> _seen = [];

    /// <summary>The declared types, in declaration order.</summary>
    public IReadOnlyList<Type> Types => _types;

    /// <summary>Records <paramref name="types"/>, ignoring any already recorded.</summary>
    /// <param name="types">Service types declared shared.</param>
    public void AddRange(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        foreach (var type in types)
        {
            if (_seen.Add(type))
            {
                _types.Add(type);
            }
        }
    }
}

namespace Rag.NET.Search;

/// <summary>
/// Thread-safe, runtime-updatable bidirectional synonym dictionary.
/// Terms are normalized to lowercase. Any term in a group expands to all other terms in that group.
/// </summary>
public sealed class SynonymMap : IDisposable
{
    private readonly Dictionary<string, HashSet<string>> _lookup =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ReaderWriterLockSlim _lock = new();

    public SynonymMap() { }

    public SynonymMap(IEnumerable<IReadOnlyCollection<string>> groups)
    {
        foreach (var group in groups)
            AddGroup([.. group]);
    }

    /// <summary>
    /// Adds a synonym group. All terms in the group become bidirectional synonyms.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when fewer than 2 terms are supplied.</exception>
    public void AddGroup(params string[] terms)
    {
        if (terms.Length < 2)
            throw new ArgumentException("A synonym group must contain at least 2 terms.", nameof(terms));

        var normalized = Array.ConvertAll(terms, t => t.ToLowerInvariant());

        _lock.EnterWriteLock();
        try
        {
            foreach (var term in normalized)
            {
                if (!_lookup.TryGetValue(term, out var synonyms))
                {
                    synonyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _lookup[term] = synonyms;
                }

                foreach (var other in normalized)
                    if (!string.Equals(term, other, StringComparison.OrdinalIgnoreCase))
                        synonyms.Add(other);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes all listed terms from the lookup. Unknown terms are silently ignored.
    /// </summary>
    public void RemoveGroup(params string[] terms)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var term in terms)
                _lookup.Remove(term.ToLowerInvariant());
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns all synonyms for <paramref name="term"/>. Returns an empty set when the term has no synonyms.
    /// </summary>
    public IReadOnlySet<string> Expand(string term)
    {
        _lock.EnterReadLock();
        try
        {
            return _lookup.TryGetValue(term.ToLowerInvariant(), out var synonyms)
                ? synonyms
                : EmptySet;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose() => _lock.Dispose();

#pragma warning disable HLQ001 // IReadOnlySet<T> is the correct abstraction here; boxing the empty sentinel is a one-time, non-hot-path cost
    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#pragma warning restore HLQ001
}

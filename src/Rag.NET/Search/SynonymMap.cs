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

    // Maximum number of whitespace-separated tokens among all registered keys.
    // Used by Tokenize to bound the phrase-scan window to O(n * maxPhraseLen)
    // instead of O(n²). Zero means no multi-word keys are registered.
    private int _maxKeyTokenCount;

    /// <summary>
    /// The maximum number of tokens (space-separated words) among all registered synonym keys.
    /// <see cref="InMemoryBm25Index"/> uses this to bound the phrase-scan window during tokenization.
    /// </summary>
    public int MaxKeyTokenCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _maxKeyTokenCount; }
            finally { _lock.ExitReadLock(); }
        }
    }

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

                // Track the longest key so Tokenize can bound its phrase-scan window.
                var tokenCount = CountTokens(term);
                if (tokenCount > _maxKeyTokenCount)
                    _maxKeyTokenCount = tokenCount;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes all listed terms from the lookup, including back-references from any remaining synonyms.
    /// Unknown terms are silently ignored.
    /// </summary>
    public void RemoveGroup(params string[] terms)
    {
        var normalized = Array.ConvertAll(terms, t => t.ToLowerInvariant());

        _lock.EnterWriteLock();
        try
        {
            foreach (var term in normalized)
            {
                _lookup.Remove(term);

                // Remove back-references from any surviving synonym sets.
                foreach (var set in _lookup.Values)
                    set.Remove(term);
            }

            // Recompute max phrase length since a key may have been removed.
            _maxKeyTokenCount = 0;
            foreach (var key in _lookup.Keys)
            {
                var count = CountTokens(key);
                if (count > _maxKeyTokenCount)
                    _maxKeyTokenCount = count;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns all synonyms for <paramref name="term"/>. Returns an empty set when the term has no synonyms.
    /// The returned set is a snapshot — safe to iterate after this call returns.
    /// </summary>
    public IReadOnlySet<string> Expand(string term)
    {
        _lock.EnterReadLock();
        try
        {
            return _lookup.TryGetValue(term.ToLowerInvariant(), out var synonyms)
                ? new HashSet<string>(synonyms, StringComparer.OrdinalIgnoreCase)
                : EmptySet;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose() => _lock.Dispose();

    private static int CountTokens(string term)
    {
        var count = 0;
        var inToken = false;
        foreach (var c in term)
        {
            if (char.IsWhiteSpace(c)) { inToken = false; }
            else if (!inToken) { inToken = true; count++; }
        }
        return count;
    }

#pragma warning disable HLQ001 // IReadOnlySet<T> is the correct abstraction here; boxing the empty sentinel is a one-time, non-hot-path cost
    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#pragma warning restore HLQ001
}

using Rag.NET.DataProviders.Testing;
using Rag.NET.Models;
using Xunit;
using Xunit.Sdk;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Tests;

/// <summary>
/// Self-test for <see cref="MetadataContract"/>. A contract check that cannot fail is worse
/// than none — every rule is verified by construction, by feeding the checker a dictionary
/// that deliberately breaks exactly that rule and asserting it rejects it.
/// </summary>
public sealed class MetadataContractTests
{
    private static Dictionary<string, MetadataValue> Valid() =>
        new(StringComparer.Ordinal) { ["repo"] = "acme/widgets" };

    /// <summary>Asserts the checker rejects <paramref name="metadata"/>, naming <paramref name="expectedReason"/>.</summary>
    private static void AssertRejects(IReadOnlyDictionary<string, MetadataValue>? metadata, string expectedReason)
    {
        var ex = Assert.ThrowsAny<XunitException>(() => MetadataContract.AssertValid(metadata, "self-test"));
        Assert.Contains(expectedReason, ex.Message, StringComparison.Ordinal);
    }

    // --- the accepted shapes ---

    [Fact]
    public void AssertValid_Null_IsAccepted()
        => MetadataContract.AssertValid((IReadOnlyDictionary<string, MetadataValue>?)null);

    [Fact]
    public void AssertValid_WellFormedDictionary_IsAccepted()
        => MetadataContract.AssertValid(new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["repo"] = "acme/widgets",
            ["change_status"] = "modified",
            ["has_attachments"] = "false",
            ["depth2"] = "1",
            ["a"] = "single-char key is legal",
        });

    // --- rule: nothing to add is null, not an empty dictionary ---

    [Fact]
    public void AssertValid_EmptyDictionary_IsRejected()
        => AssertRejects(new Dictionary<string, MetadataValue>(StringComparer.Ordinal), "the dictionary is empty");

    // --- rule: the comparer is StringComparer.Ordinal ---

    [Fact]
    public void AssertValid_OrdinalIgnoreCaseComparer_IsRejected()
        => AssertRejects(
            new Dictionary<string, MetadataValue>(StringComparer.OrdinalIgnoreCase) { ["repo"] = "acme/widgets" },
            "rather than StringComparer.Ordinal");

#pragma warning disable MA0002 // The missing comparer IS the violation under test.
    [Fact]
    public void AssertValid_DefaultComparer_IsRejected()
        => AssertRejects(new Dictionary<string, MetadataValue> { ["repo"] = "acme/widgets" },
            "rather than StringComparer.Ordinal");
#pragma warning restore MA0002

    [Fact]
    public void AssertValid_NonDictionaryImplementation_IsRejected()
        => AssertRejects(new UnverifiableDictionary(Valid()), "whose comparer cannot be verified");

    // --- rule: keys are snake_case ---

    [Theory]
    [InlineData("Repo")]                 // uppercase
    [InlineData("REPO")]
    [InlineData("changeStatus")]         // camelCase
    [InlineData("change-status")]        // hyphen
    [InlineData("change.status")]        // dot
    [InlineData("change status")]        // space
    [InlineData("_internal")]            // leading underscore is framework-internal
    [InlineData("trailing_")]            // trailing underscore
    public void AssertValid_NonSnakeCaseKey_IsRejected(string key)
        => AssertRejects(new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { [key] = "v" },
            "is not snake_case");

    // --- rule: no reserved key ---

    [Theory]
    [InlineData(ReservedMetadataKeys.DocumentId)]
    [InlineData(ReservedMetadataKeys.FileName)]
    [InlineData(ReservedMetadataKeys.CreatedAt)]
    [InlineData(ReservedMetadataKeys.UpdatedAt)]
    [InlineData(ReservedMetadataKeys.ProviderId)]
    [InlineData(ReservedMetadataKeys.ParentKey)]
    [InlineData(ReservedMetadataKeys.AllowedRoles)]
    [InlineData(ReservedMetadataKeys.TrustLevel)]
    public void AssertValid_ReservedKey_IsRejected(string key)
        => AssertRejects(new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { [key] = "v" },
            "is reserved by the framework");

    /// <summary>
    /// Pins the reserved set itself. If a key is added to or removed from
    /// <see cref="ReservedMetadataKeys"/> without the design being revisited, this fails.
    /// Phase 4.10 Task 4 grew this from seven to eight, reserving <c>updated_at</c> once the five
    /// connectors that used to write it as a plain tag were migrated to
    /// <c>FileHandle.UpdatedAt</c> in the same commit. The typed-metadata change (#91/#82) grew
    /// it to ten, reserving <c>page</c>/<c>page_end</c> for the chunking strategies' source-page
    /// attribution.
    /// </summary>
    [Fact]
    public void ReservedKeys_AreExactlyTheTenFromTheDesign()
        => Assert.Equal(
            ["_parentKey", "allowed_roles", "created_at", "document_id", "file_name", "page", "page_end", "provider_id", "trust_level", "updated_at"],
            ReservedMetadataKeys.All.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    // --- rule: no null or empty values ---

    [Fact]
    public void AssertValid_EmptyValue_IsRejected()
        => AssertRejects(new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["repo"] = "" },
            "has a null or empty value");

    [Fact]
    public void AssertValid_NullValue_IsRejected()
        => AssertRejects(new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["repo"] = null! },
            "has a null or empty value");

    // --- all violations are reported at once, not just the first ---

    [Fact]
    public void AssertValid_MultipleViolations_AreAllReported()
    {
        var ex = Assert.ThrowsAny<XunitException>(() => MetadataContract.AssertValid(
            new Dictionary<string, MetadataValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Repo"] = "acme/widgets",
                ["created_at"] = "2026-01-01",
                ["empty"] = "",
            },
            "self-test"));

        Assert.Contains("rather than StringComparer.Ordinal", ex.Message, StringComparison.Ordinal);
        Assert.Contains("is not snake_case", ex.Message, StringComparison.Ordinal);
        Assert.Contains("is reserved by the framework", ex.Message, StringComparison.Ordinal);
        Assert.Contains("has a null or empty value", ex.Message, StringComparison.Ordinal);
    }

    // --- the FileEntry conveniences delegate to the same rules ---

    private static FileEntry Entry(IReadOnlyDictionary<string, MetadataValue>? metadata) => new(
        Id: new EntryId("entry-1"),
        FileName: "doc.md",
        OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream()),
        Metadata: metadata);

    [Fact]
    public void AssertValid_FileEntry_RejectsBadMetadataAndNamesTheEntry()
    {
        var ex = Assert.ThrowsAny<XunitException>(() => MetadataContract.AssertValid(
            Entry(new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["Repo"] = "x" })));

        Assert.Contains("entry-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("is not snake_case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertValid_FileEntryWithNullMetadata_IsAccepted()
        => MetadataContract.AssertValid(Entry(null));

    [Fact]
    public void AssertAll_RejectsWhenAnyEntryViolates()
    {
        var entries = new[]
        {
            Entry(Valid()),
            Entry(null),
            Entry(new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["trust_level"] = "public" }),
        };

        var ex = Assert.ThrowsAny<XunitException>(() => MetadataContract.AssertAll(entries));
        Assert.Contains("is reserved by the framework", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertAll_AcceptsAllValidEntries()
        => MetadataContract.AssertAll([Entry(Valid()), Entry(null)]);

    // --- the Result overload: the shape GetFilesAsync().ToListAsync() actually yields ---

    [Fact]
    public void AssertAllResults_RejectsWhenAnySuccessViolates()
    {
        var results = new[]
        {
            Result<FileEntry, RagError>.Success(Entry(Valid())),
            Result<FileEntry, RagError>.Success(
                Entry(new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["Repo"] = "x" })),
        };

        var ex = Assert.ThrowsAny<XunitException>(() => MetadataContract.AssertAll(results));
        Assert.Contains("is not snake_case", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Failure results carry no metadata to check, and whether a connector is supposed to fail
    /// on a fixture is a per-connector question. They must be skipped, not thrown on.
    /// </summary>
    [Fact]
    public void AssertAllResults_SkipsFailureResults()
    {
        var results = new[]
        {
            Result<FileEntry, RagError>.Failure(
                new RagError.HttpFailed(System.Net.HttpStatusCode.Unauthorized, null)),
            Result<FileEntry, RagError>.Success(Entry(Valid())),
            Result<FileEntry, RagError>.Success(Entry(null)),
        };

        MetadataContract.AssertAll(results);
    }

    /// <summary>
    /// An <see cref="IReadOnlyDictionary{TKey,TValue}"/> that is not a
    /// <see cref="Dictionary{TKey,TValue}"/>, so its comparer cannot be verified.
    /// </summary>
    private sealed class UnverifiableDictionary(Dictionary<string, MetadataValue> inner)
        : IReadOnlyDictionary<string, MetadataValue>
    {
        public MetadataValue this[string key] => inner[key];
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<MetadataValue> Values => inner.Values;
        public int Count => inner.Count;
        public bool ContainsKey(string key) => inner.ContainsKey(key);
        public bool TryGetValue(string key, out MetadataValue value) => inner.TryGetValue(key, out value);
        IEnumerator<KeyValuePair<string, MetadataValue>> IEnumerable<KeyValuePair<string, MetadataValue>>.GetEnumerator()
            => inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => inner.GetEnumerator();
    }
}

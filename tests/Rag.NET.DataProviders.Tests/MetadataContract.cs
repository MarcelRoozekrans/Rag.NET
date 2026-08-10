using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Testing;

/// <summary>
/// The shared connector-metadata contract check. Every connector test suite applies it to the
/// metadata its provider emits; per-connector tests pin the actual key/value pairs, but only a
/// shared check catches convention drift across 21 connectors.
/// <para>
/// <b>This file lives in <c>tests/Rag.NET.DataProviders.Tests</c> and is <i>linked</i></b> into
/// every other connector test project — there is no shared unit-test library, and referencing
/// one test project from another would drag its whole assembly along. The link follows the
/// pattern already used for <c>src/Shared/GraphErrorMapping.cs</c>:
/// <code>
/// &lt;Compile Include="..\Rag.NET.DataProviders.Tests\MetadataContract.cs" Link="MetadataContract.cs" /&gt;
/// </code>
/// It deliberately names only <see cref="FileEntry"/> (which lives in <c>Rag.NET</c>) and never
/// <c>FileHandle</c> (which lives in <c>Rag.NET.DataProviders</c>): the Web connector references
/// <c>Rag.NET</c> directly and would not compile otherwise. To check a handle, pass its
/// dictionary: <c>MetadataContract.AssertValid(handle.Metadata, handle.Id)</c>.
/// </para>
/// <para>The rules are the convention from the Phase 2.2 design, §1.</para>
/// </summary>
internal static class MetadataContract
{
    /// <summary>
    /// Asserts <paramref name="metadata"/> satisfies the connector-metadata convention.
    /// <c>null</c> is valid and is the <i>only</i> way to say "nothing to add".
    /// </summary>
    /// <param name="metadata">The dictionary a connector put on its handle or entry.</param>
    /// <param name="context">
    /// Optional identifier (entry id, connector name) echoed into the failure message.
    /// </param>
    public static void AssertValid(IReadOnlyDictionary<string, MetadataValue>? metadata, string? context = null)
    {
        // null is the sanctioned "nothing to add" representation — the pipeline branches on it.
        if (metadata is null)
            return;

        var violations = new List<string>();
        CollectShapeViolations(metadata, violations);

        foreach (var (key, value) in metadata)
            CollectEntryViolations(key, value, violations);

        if (violations.Count == 0)
            return;

        var where = string.IsNullOrEmpty(context) ? string.Empty : $" for '{context}'";
        Assert.Fail(
            $"Connector metadata contract violated{where}:{Environment.NewLine}  - " +
            string.Join($"{Environment.NewLine}  - ", violations));
    }

    /// <summary>Asserts <paramref name="entry"/>'s metadata satisfies the convention.</summary>
    public static void AssertValid(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AssertValid(entry.Metadata, entry.Id.Value);
    }

    /// <summary>Asserts every entry's metadata satisfies the convention.</summary>
    public static void AssertAll(IEnumerable<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
            AssertValid(entry);
    }

    /// <summary>
    /// Asserts every successful result's metadata satisfies the convention. This is the shape
    /// <c>GetFilesAsync().ToListAsync()</c> actually yields, so connector suites can pass it
    /// straight through instead of hand-rolling the unwrap.
    /// </summary>
    /// <remarks>
    /// Failure results are skipped, not asserted on: whether a connector is <i>supposed</i> to
    /// fail on a given fixture is a per-connector question this shared check has no view on.
    /// </remarks>
    public static void AssertAll(IEnumerable<Result<FileEntry, RagError>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (var result in results)
        {
            if (result.IsSuccess)
                AssertValid(result.Value);
        }
    }

    /// <summary>Rules about the dictionary itself rather than its contents.</summary>
    private static void CollectShapeViolations(
        IReadOnlyDictionary<string, MetadataValue> metadata, List<string> violations)
    {
        if (metadata.Count == 0)
        {
            violations.Add(
                "the dictionary is empty. A connector with nothing to add must leave Metadata null — " +
                "the pipeline branches on 'is not null', so empty is a second representation of the same thing.");
        }

        // DocumentMetadata.Tags and BuildMetadata are ordinal; a mismatched comparer would make
        // lookups inconsistent between the connector and the pipeline.
        if (metadata is not Dictionary<string, MetadataValue> concrete)
        {
            violations.Add(
                $"the dictionary is a {metadata.GetType().Name}, whose comparer cannot be verified. " +
                "Build it as new Dictionary<string, MetadataValue>(StringComparer.Ordinal).");
        }
        else if (!ReferenceEquals(concrete.Comparer, StringComparer.Ordinal))
        {
            violations.Add(
                $"the dictionary uses {concrete.Comparer.GetType().Name} rather than StringComparer.Ordinal. " +
                "Build it as new Dictionary<string, MetadataValue>(StringComparer.Ordinal).");
        }
    }

    /// <summary>Rules about a single key/value pair.</summary>
    private static void CollectEntryViolations(string key, MetadataValue value, List<string> violations)
    {
        if (ReservedMetadataKeys.IsReserved(key))
        {
            violations.Add(
                $"key '{key}' is reserved by the framework and would shadow the value the pipeline writes " +
                $"(connector tags are applied first, with TryAdd). Reserved keys: " +
                $"{string.Join(", ", ReservedMetadataKeys.All)}.");
        }
        else if (!IsSnakeCase(key))
        {
            // Checked only when not reserved so '_parentKey' reports the more useful reason.
            violations.Add(
                $"key '{key}' is not snake_case. Keys are lowercase letters, digits and underscores, " +
                "with no leading or trailing underscore (a leading underscore is framework-internal).");
        }

        // Only a string can be empty; a number, boolean or date always carries a real value.
        if (value.Kind == MetadataValueKind.String && value.StringValue.Length == 0)
        {
            violations.Add(
                $"key '{key}' has a null or empty value. Optional fields are omitted, never written " +
                "empty — an empty value is indistinguishable from a real one at query time.");
        }
    }

    /// <summary>
    /// Deliberately hand-rolled rather than a <c>[GeneratedRegex]</c>: this file is linked into
    /// 22 assemblies, and a generated regex would pull the regex source generator into every
    /// connector test project and force <c>partial</c> plumbing through all of them. The loop
    /// allocates nothing and is the same handful of lines. Please leave it alone.
    /// </summary>
    private static bool IsSnakeCase(string key)
    {
        if (key.Length == 0 || key[0] == '_' || key[^1] == '_')
            return false;

        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_')
                return false;
        }

        return true;
    }
}

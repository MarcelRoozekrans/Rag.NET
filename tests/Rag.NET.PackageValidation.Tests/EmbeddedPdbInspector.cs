using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Reads the debug information inside a packed assembly and reports what would stop Go to
/// Definition from landing in the original source (#1030).
/// </summary>
/// <remarks>
/// <para>
/// An embedded PDB is necessary and not sufficient. An embedded PDB with no Source Link map, or
/// one whose documents still carry the build machine's paths, shows decompiled code exactly like
/// no PDB at all. So the check goes further than "a PDB is there": the build is deterministic,
/// the Source Link map is present, every URL in it points at this repository, and every source
/// document resolves through the map.
/// </para>
/// <para>
/// Adapted from <c>scripts/check-embedded-pdb/check-embedded-pdb.cs</c> in
/// <c>MarcelRoozekrans/BlazorNative</c>, which does the same for that repository's packages.
/// <c>System.Reflection.Metadata</c> does all the reading; nothing here parses PDB bytes by hand
/// except the small embedded-source blob header the Portable PDB specification defines.
/// </para>
/// </remarks>
internal static class EmbeddedPdbInspector
{
    // Custom debug information kinds, from the Portable PDB specification.
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    private static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    /// <summary>
    /// Finds what is wrong with the debug information of one assembly.
    /// </summary>
    /// <param name="assembly">A seekable stream over the assembly. It is left open.</param>
    /// <param name="expectedSourceLinkPrefix">
    /// What every Source Link URL must start with, such as
    /// <c>https://raw.githubusercontent.com/MarcelRoozekrans/Rag.NET/</c>.
    /// </param>
    /// <param name="checkSourceLink">
    /// Whether to check the Source Link map and the <c>/_/</c> document roots. Both hold only for a
    /// build made with <c>ContinuousIntegrationBuild</c>, which this repository sets on GitHub
    /// Actions only, so a local pack must pass <see langword="false"/>.
    /// </param>
    /// <returns>
    /// Each problem as a phrase that continues "the assembly ...". Empty when there is none.
    /// </returns>
    internal static IReadOnlyList<string> FindProblems(
        Stream assembly,
        string expectedSourceLinkPrefix,
        bool checkSourceLink)
    {
        var problems = new List<string>();

        using var pe = new PEReader(assembly, PEStreamOptions.LeaveOpen);

        var reproducible = false;
        var hasEmbeddedPdb = false;
        DebugDirectoryEntry embeddedPdb = default;
        foreach (var entry in pe.ReadDebugDirectory())
        {
            if (entry.Type == DebugDirectoryEntryType.Reproducible)
            {
                reproducible = true;
            }
            else if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb && !hasEmbeddedPdb)
            {
                hasEmbeddedPdb = true;
                embeddedPdb = entry;
            }
        }

        if (!reproducible)
        {
            problems.Add("is not a deterministic build: its debug directory has no Reproducible entry");
        }

        if (!hasEmbeddedPdb)
        {
            problems.Add("has no embedded PDB, so Go to Definition into it shows decompiled code");
            return problems;
        }

        if (checkSourceLink)
        {
            AddSourceLinkProblems(pe, embeddedPdb, expectedSourceLinkPrefix, problems);
        }

        return problems;
    }

    /// <summary>
    /// Reads the <c>documents</c> map of a Source Link JSON document.
    /// </summary>
    /// <param name="json">The Source Link JSON stored in the PDB.</param>
    /// <param name="problems">Where each problem found is added.</param>
    /// <returns>The key and URL pairs, or <see langword="null"/> when the map is unusable.</returns>
    internal static List<(string Key, string Url)>? ParseSourceLinkMap(string json, List<string> problems)
    {
        var map = new List<(string Key, string Url)>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("documents", out var documents)
                || documents.ValueKind != JsonValueKind.Object)
            {
                problems.Add("has a Source Link map without a documents object");
                return null;
            }

            foreach (var entry in documents.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    problems.Add($"has the Source Link entry {entry.Name}, whose URL is not a string");
                    continue;
                }

                // Source Link specification: a key and its URL both hold no * or both hold exactly
                // one. The key's * must be its last character; the URL's may sit anywhere, as in
                // .../*?raw=1.
                var url = entry.Value.GetString()!;
                var keyWildcards = CountWildcards(entry.Name);
                var urlWildcards = CountWildcards(url);
                if (keyWildcards > 1
                    || keyWildcards != urlWildcards
                    || (keyWildcards == 1 && !entry.Name.EndsWith('*')))
                {
                    problems.Add($"has the Source Link entry {entry.Name} -> {url}, which breaks the wildcard rule");
                    continue;
                }

                map.Add((entry.Name, url));
            }
        }
        catch (JsonException e)
        {
            problems.Add($"has a Source Link map that is not valid JSON: {e.Message}");
            return null;
        }

        if (map.Count == 0 && problems.Count == 0)
        {
            problems.Add("has a Source Link map with no documents");
        }

        return problems.Count == 0 ? map : null;
    }

    /// <summary>
    /// Resolves a document path through a Source Link map.
    /// </summary>
    /// <remarks>
    /// Source Link specification: paths match case-insensitively. An exact key wins; otherwise the
    /// longest wildcard key whose prefix matches, and the rest of the path, in its original case,
    /// replaces the <c>*</c> in the URL.
    /// </remarks>
    /// <param name="map">The pairs from <see cref="ParseSourceLinkMap"/>.</param>
    /// <param name="path">The document path recorded in the PDB.</param>
    /// <returns>The URL the path maps to, or <see langword="null"/> when no entry matches.</returns>
    internal static string? ResolveSourceLink(List<(string Key, string Url)> map, string path)
    {
        foreach (var (key, url) in map)
        {
            if (!key.EndsWith('*') && string.Equals(key, path, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        string? best = null;
        var bestLength = -1;
        foreach (var (key, url) in map)
        {
            if (!key.EndsWith('*'))
            {
                continue;
            }

            var prefix = key[..^1];
            if (prefix.Length > bestLength && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                best = url.Replace("*", path[prefix.Length..], StringComparison.Ordinal);
                bestLength = prefix.Length;
            }
        }

        return best;
    }

    private static void AddSourceLinkProblems(
        PEReader pe,
        DebugDirectoryEntry embeddedPdb,
        string expectedSourceLinkPrefix,
        List<string> problems)
    {
        // Decompresses the PDB, and throws when its size differs from the size the entry declares.
        using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdb);
        var pdb = provider.GetMetadataReader();

        var (sourceLinkJson, withEmbeddedSource) = ReadCustomDebugInformation(pdb);
        if (sourceLinkJson is null)
        {
            problems.Add("has an embedded PDB without a Source Link map");
            return;
        }

        var map = ParseSourceLinkMap(sourceLinkJson, problems);
        if (map is null)
        {
            return;
        }

        foreach (var (key, url) in map)
        {
            if (!url.StartsWith(expectedSourceLinkPrefix, StringComparison.Ordinal))
            {
                problems.Add($"maps {key} to {url}, which does not start with {expectedSourceLinkPrefix}");
            }
        }

        AddDocumentProblems(pdb, map, withEmbeddedSource, problems);
    }

    // The module's Source Link JSON, and the documents that carry their own source.
    private static (string? SourceLinkJson, HashSet<DocumentHandle> WithEmbeddedSource) ReadCustomDebugInformation(
        MetadataReader pdb)
    {
        string? sourceLinkJson = null;
        var withEmbeddedSource = new HashSet<DocumentHandle>();
        foreach (var handle in pdb.CustomDebugInformation)
        {
            var info = pdb.GetCustomDebugInformation(handle);
            var kind = pdb.GetGuid(info.Kind);
            if (kind == SourceLinkKind && info.Parent.Kind == HandleKind.ModuleDefinition)
            {
                sourceLinkJson = Encoding.UTF8.GetString(pdb.GetBlobBytes(info.Value));
            }
            else if (kind == EmbeddedSourceKind
                && info.Parent.Kind == HandleKind.Document
                && IsEmbeddedSource(pdb.GetBlobBytes(info.Value)))
            {
                withEmbeddedSource.Add((DocumentHandle)info.Parent);
            }
        }

        return (sourceLinkJson, withEmbeddedSource);
    }

    // A document with embedded source needs no URL. Every other document must sit under the
    // deterministic /_/ root that ContinuousIntegrationBuild writes, and resolve through the map.
    private static void AddDocumentProblems(
        MetadataReader pdb,
        List<(string Key, string Url)> map,
        HashSet<DocumentHandle> withEmbeddedSource,
        List<string> problems)
    {
        foreach (var handle in pdb.Documents)
        {
            var name = pdb.GetString(pdb.GetDocument(handle).Name);
            if (withEmbeddedSource.Contains(handle))
            {
                continue;
            }

            if (!name.StartsWith("/_/", StringComparison.Ordinal))
            {
                problems.Add($"has the document {name}, which is not under /_/; was it built with ContinuousIntegrationBuild?");
            }
            else if (ResolveSourceLink(map, name) is null)
            {
                problems.Add($"has the document {name}, which no Source Link entry maps");
            }
        }
    }

    private static int CountWildcards(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c == '*')
            {
                count++;
            }
        }

        return count;
    }

    // Portable PDB specification: embedded source is a 4-byte format, then the text. Format 0 means
    // the text is stored raw; a positive format is the text's length, compressed with deflate. Only
    // a well-formed blob exempts its document from the map.
    private static bool IsEmbeddedSource(byte[] blob)
    {
        if (blob.Length < 4)
        {
            return false;
        }

        var format = BinaryPrimitives.ReadInt32LittleEndian(blob);
        if (format <= 0)
        {
            return format == 0;
        }

        try
        {
            using var deflate = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
            using var text = new MemoryStream();
            deflate.CopyTo(text);
            return text.Length == format;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

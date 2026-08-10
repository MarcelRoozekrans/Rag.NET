using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

/// <summary>
/// Splits code files at language-appropriate boundaries (class, function, method) using
/// per-language separator hierarchies. Language is detected from the file extension in
/// <c>DocumentSection.DocumentId.Value</c> or set explicitly via <see cref="CodeChunkingOptions.Language"/>.
/// Unknown extensions fall back to generic code separators (<c>\n\n</c>, <c>\n</c>, space).
/// </summary>
public sealed class CodeChunkingStrategy : IChunkingStrategy
{
    private static readonly Dictionary<string, string[]> LanguageSeparators =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["python"]     = ["\nclass ", "\ndef ", "\n\tdef ", "\n\n", "\n", " "],
            ["javascript"] = ["\nfunction ", "\nclass ", "\nconst ", "\nlet ", "\n\n", "\n", " "],
            ["typescript"] = ["\nfunction ", "\nclass ", "\ninterface ", "\ntype ", "\nconst ", "\n\n", "\n", " "],
            ["java"]       = ["\npublic class ", "\nprivate ", "\nprotected ", "\npublic ", "\nvoid ", "\n\n", "\n", " "],
            ["go"]         = ["\nfunc ", "\ntype ", "\nvar ", "\nconst ", "\n\n", "\n", " "],
            ["rust"]       = ["\nfn ", "\nimpl ", "\nstruct ", "\nenum ", "\ntrait ", "\n\n", "\n", " "],
            ["ruby"]       = ["\ndef ", "\nclass ", "\nmodule ", "\n\n", "\n", " "],
            ["csharp"]     = ["\npublic class ", "\nprivate ", "\nprotected ", "\npublic ", "\nnamespace ", "\n\n", "\n", " "],
            ["cpp"]        = ["\nvoid ", "\nclass ", "\nstruct ", "\nnamespace ", "\n\n", "\n", " "],
            ["php"]        = ["\nfunction ", "\nclass ", "\n\n", "\n", " "],
            ["swift"]      = ["\nfunc ", "\nclass ", "\nstruct ", "\nextension ", "\n\n", "\n", " "],
        };

    private static readonly string[] GenericCodeSeparators = ["\n\n", "\n", " "];

    private static readonly Dictionary<string, string> ExtensionToLanguage =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".py"]   = "python",
            [".js"]   = "javascript",  [".mjs"] = "javascript", [".cjs"] = "javascript",
            [".ts"]   = "typescript",  [".tsx"] = "typescript",
            [".java"] = "java",
            [".go"]   = "go",
            [".rs"]   = "rust",
            [".rb"]   = "ruby",
            [".cs"]   = "csharp",
            [".cpp"]  = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp", [".h"] = "cpp", [".hpp"] = "cpp",
            [".php"]  = "php",
            [".swift"] = "swift",
        };

    private readonly CodeChunkingOptions _options;

    public CodeChunkingStrategy(CodeChunkingOptions options)
    {
        if (options.Language is not null && !LanguageSeparators.ContainsKey(options.Language))
            throw new ArgumentException(
                $"Unrecognised language '{options.Language}'. " +
                $"Valid values: {string.Join(", ", LanguageSeparators.Keys)}.",
                nameof(options));
        _options = options;
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(section.Text))
            yield break;

        var language   = _options.Language ?? DetectLanguage(section);
        var separators = language is not null && LanguageSeparators.TryGetValue(language, out var seps)
            ? seps
            : GenericCodeSeparators;

        var chunkIndex = 0;
        foreach (var text in SplitRecursively(section.Text, options.MaxChunkSize, separators, 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextChunk
            {
                Text       = text,
                DocumentId = section.DocumentId,
                ChunkIndex = chunkIndex++,
                Metadata   = PageMetadata.ForPage(section.PageNumber),
            };
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string? DetectLanguage(DocumentSection section)
    {
        var ext = Path.GetExtension(section.DocumentId.Value);
        return !string.IsNullOrEmpty(ext) && ExtensionToLanguage.TryGetValue(ext, out var lang)
            ? lang
            : null;
    }

    private static IEnumerable<string> SplitRecursively(
        string text, int maxSize, string[] separators, int sepIndex)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            yield break;

        if (sepIndex >= separators.Length)
        {
            if (trimmed.Length <= maxSize)
            {
                yield return trimmed;
                yield break;
            }
            for (int i = 0; i < trimmed.Length; i += maxSize)
            {
                var seg = trimmed.Substring(i, Math.Min(maxSize, trimmed.Length - i)).Trim();
                if (seg.Length > 0)
                    yield return seg;
            }
            yield break;
        }

        var sep = separators[sepIndex];
        // Split while keeping separator prefix with subsequent chunks
        var segments = SplitKeepingSeparator(trimmed, sep);

        if (segments.Length <= 1)
        {
            // This separator didn't divide the text; try the next
            foreach (var chunk in SplitRecursively(trimmed, maxSize, separators, sepIndex + 1))
                yield return chunk;
            yield break;
        }

        foreach (var segment in segments)
        {
            var s = segment.Trim();
            if (s.Length == 0) continue;

            if (s.Length <= maxSize)
            {
                yield return s;
            }
            else
            {
                foreach (var chunk in SplitRecursively(s, maxSize, separators, sepIndex + 1))
                    yield return chunk;
            }
        }
    }

    /// <summary>
    /// Splits <paramref name="text"/> on <paramref name="separator"/>, keeping the separator
    /// as a prefix of each subsequent segment (so boundary keywords are preserved).
    /// </summary>
    private static string[] SplitKeepingSeparator(string text, string separator)
    {
        var result = new List<string>();
        int idx;

        // Find the first occurrence; everything before it is the first segment
        idx = text.IndexOf(separator, StringComparison.Ordinal);
        if (idx < 0)
        {
            return [text];
        }

        // First chunk: everything before the first separator
        if (idx > 0)
            result.Add(text[..idx]);

        // Remaining chunks: separator + content up to next separator
        while (idx >= 0)
        {
            int next = text.IndexOf(separator, idx + separator.Length, StringComparison.Ordinal);
            int end  = next >= 0 ? next : text.Length;
            result.Add(text[idx..end]);
            idx = next;
        }

        return result.ToArray();
    }
}

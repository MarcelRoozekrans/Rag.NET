namespace Rag.NET.DataProviders;

internal static class FileExtensionMatcher
{
    public static bool Matches(string fileName, IReadOnlyList<string> extensions)
    {
        if (extensions is ["*"]) return true;
        var ext = Path.GetExtension(fileName);
        return extensions.Any(e =>
            string.Equals(e, ext, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e, "*", StringComparison.Ordinal));
    }
}

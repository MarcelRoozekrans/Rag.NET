using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Gmail;

public sealed class GmailOptions : CloudStorageOptions
{
    public string UserName   { get; init; } = string.Empty;
    public string Query      { get; init; } = "in:inbox";
    public int    MaxResults { get; init; } = 500;
}

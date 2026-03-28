namespace Rag.NET.DataProviders;

/// <summary>Returns a fixed pre-issued token (API key, PAT, SAS token).</summary>
public sealed class StaticTokenProvider(string token) : ITokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(token);
}

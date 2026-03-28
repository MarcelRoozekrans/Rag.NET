namespace Rag.NET.DataProviders;

/// <summary>Returns a fixed pre-issued token (API key, PAT, SAS token).</summary>
public sealed class StaticTokenProvider : ITokenProvider
{
    private readonly string _token;

    public StaticTokenProvider(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _token = token;
    }

    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_token);
}

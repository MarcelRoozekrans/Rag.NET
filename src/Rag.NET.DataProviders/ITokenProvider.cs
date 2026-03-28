namespace Rag.NET.DataProviders;

/// <summary>Provides a bearer token for authenticating against a cloud API.</summary>
public interface ITokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

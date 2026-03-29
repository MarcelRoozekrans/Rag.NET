using Refit;

namespace Rag.NET.DataProviders.Bitbucket;

[Headers("Accept: application/json")]
internal interface IBitbucketApi
{
    [Get("/repositories/{workspace}/{repo}/src/{commit}/{path}")]
    Task<BitbucketSourcePage> GetSourceAsync(
        string workspace,
        string repo,
        string commit,
        string path,
        [Query] int? pagelen = null,
        [Query] string? page = null,
        CancellationToken cancellationToken = default);

    [Get("/repositories/{workspace}/{repo}/src/{commit}/{path}")]
    [Headers("Accept: application/octet-stream")]
    Task<HttpResponseMessage> GetRawFileAsync(
        string workspace,
        string repo,
        string commit,
        string path,
        CancellationToken cancellationToken = default);

    [Get("/repositories/{workspace}/{repo}/diffstat/{spec}")]
    Task<BitbucketDiffstatPage> GetDiffstatAsync(
        string workspace,
        string repo,
        string spec,
        [Query] int? pagelen = null,
        [Query] string? page = null,
        CancellationToken cancellationToken = default);
}

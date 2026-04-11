using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Bitbucket;

[ZeroAllocRestClient]
internal interface IBitbucketApi
{
    [Get("/2.0/repositories/{workspace}/{repo}/src/{commit}/{path}")]
    Task<Result<BitbucketSourcePage, ZeroAlloc.Rest.HttpError>> GetSourceAsync(
        string workspace,
        string repo,
        string commit,
        string path,
        [Query(Name = "pagelen")] int? pagelen = null,
        [Query(Name = "page")] string? page = null,
        CancellationToken cancellationToken = default);

    [Get("/2.0/repositories/{workspace}/{repo}/diffstat/{spec}")]
    Task<Result<BitbucketDiffstatPage, ZeroAlloc.Rest.HttpError>> GetDiffstatAsync(
        string workspace,
        string repo,
        string spec,
        [Query(Name = "pagelen")] int? pagelen = null,
        [Query(Name = "page")] string? page = null,
        CancellationToken cancellationToken = default);
}

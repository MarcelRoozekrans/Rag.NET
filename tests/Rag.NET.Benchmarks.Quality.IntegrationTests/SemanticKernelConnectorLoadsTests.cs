using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Drives Semantic Kernel's InMemory connector end to end — create, upsert, search — against a
/// stub embedder, ungated, in the fast tier on every push.
/// <para>
/// <b>The defect this exists for.</b> Renovate's #119 moved <c>Microsoft.SemanticKernel</c> from
/// 1.78.0 to 1.79.0, which requires <c>Microsoft.Extensions.VectorData.Abstractions</c> ≥ 10.5.2 —
/// and 10.5.0 had removed <c>VectorSearchFilter</c>, a type the InMemory connector still binds to.
/// Restore succeeded and the build was clean; the entrant died at runtime with
/// <c>TypeLoadException</c> the moment it touched the connector. It stayed broken on <c>main</c>
/// through a full release cycle.
/// </para>
/// <para>
/// <b>Why nothing reported it.</b> <see cref="SemanticKernelEntrantTests"/> already ran on every
/// push and passed throughout — but every case in it exercises the entrant's own arithmetic,
/// <c>RetrievalDepth</c> and <c>TopDocuments</c>, which are pure functions over
/// <see cref="ScoredDocument"/> and never load a Semantic Kernel type. The only code that touched
/// the connector sat behind <c>RAGNET_BEIR_LONG_RUNS</c> and an hour of corpus, so the entrant had
/// tests that read as coverage while the library the entrant is about could not load at all. This
/// case is the missing half: it asserts nothing about retrieval quality — that is the gated row's
/// job — only that the connector's own create/upsert/search path executes.
/// </para>
/// <para>
/// It uses a stub generator rather than the pinned ONNX embedder deliberately, so it needs no
/// model, no corpus and no gate. A dependency resolution that cannot load is a build-level fact,
/// and catching it must not cost an hour or depend on a provisioned machine.
/// </para>
/// </summary>
public sealed class SemanticKernelConnectorLoadsTests
{
    [Fact]
    public async Task TheInMemoryConnectorIndexesAndSearches_SoAVersionItCannotLoadFailsHere()
    {
        var ct = TestContext.Current.CancellationToken;
        using var collection = SemanticKernelEntrant.CreateCollection(new StubEmbeddingGenerator());

        // Two documents in distinct one-hot buckets, so the search order is decided by the
        // connector's cosine and not by a tie.
        await SemanticKernelEntrant.IndexAsync(
            collection,
            [Document("d1", "alpha"), Document("d2", "beta")],
            ct);

        var (runs, latencies) = await SemanticKernelEntrant.RetrieveAsync(
            collection,
            [new BeirQuery("q1", "alpha")],
            excludesSelfRetrievedDocument: false,
            ct);

        Assert.Equal("d1", Assert.Single(runs).Value[0].DocumentId);
        Assert.True(latencies["q1"] >= 0);
    }

    [Fact]
    public void TheDerivedRunTagNamesThePinnedPackageVersion_BecauseAnAssemblyCanMisnameItsPackage()
    {
        // Not a tautology, and this repository has the counter-example in the same dependency
        // graph: Microsoft.Extensions.VectorData.Abstractions *10.1.0* ships an assembly whose
        // informational version reads 1.74.0. Deriving the tag from assembly metadata is only
        // honest while that metadata agrees with the package a reader would install, so the two
        // are compared here rather than assumed equal.
        var pinned = PinnedPackageVersion("Microsoft.SemanticKernel");

        Assert.Equal(pinned, SemanticKernelEntrant.LoadedVersion);
        Assert.Equal("semantic-kernel-" + pinned, SemanticKernelEntrant.RunTag);
    }

    /// <summary>The version Central Package Management pins for a package.</summary>
    private static string PinnedPackageVersion(string packageId)
    {
        var props = Path.Combine(RepositoryRoot(), "Directory.Packages.props");
        var match = Regex.Match(
            File.ReadAllText(props),
            "<PackageVersion\\s+Include=\"" + Regex.Escape(packageId) + "\"\\s+Version=\"([^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(match.Success, $"'{packageId}' is no longer pinned in {props}.");
        return match.Groups[1].Value;
    }

    /// <summary>The repository root, found by the file every project's versions come from.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            "No Directory.Packages.props above " + AppContext.BaseDirectory + ".");
        return directory!.FullName;
    }

    private static BeirDocument Document(string id, string text) =>
        new(id, Title: string.Empty, Text: text, RetrievalText: text);

    /// <summary>
    /// A deterministic one-hot embedder: every text maps to the unit vector at the bucket its
    /// first character picks, so identical texts score 1.0 and different ones 0.0. Enough for the
    /// connector to rank, and free of any model.
    /// </summary>
    private sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                var vector = new float[SemanticKernelEntrant.EmbeddingDimension];
                vector[value.Length == 0 ? 0 : value[0] % vector.Length] = 1f;
                embeddings.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

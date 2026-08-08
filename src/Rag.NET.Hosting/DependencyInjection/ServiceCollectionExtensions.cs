using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Hosting.Configuration;
using Rag.NET.PgVector;
using Rag.NET.Qdrant;
using Rag.NET.Storage;

namespace Rag.NET.Hosting.DependencyInjection;

/// <summary>
/// Wires a full <see cref="IRagPipeline"/> — chat client, embedding generator, and vector store —
/// from an <see cref="IConfiguration"/>'s <c>RagNet</c> section, so an executable's pipeline
/// behaviour lives in a library method rather than in <c>Program.cs</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds <c>RagNet</c> and registers <see cref="IRagPipeline"/>: an OpenAI-compatible chat
    /// client and embedding generator (covers OpenAI, Azure OpenAI, OpenRouter, Ollama, and LM
    /// Studio), plus one of three vector-store kinds — <c>InMemory</c>, <c>Qdrant</c>, or
    /// <c>PgVector</c>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">The configuration root the <c>RagNet</c> section is read from.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// No startup validation runs yet: a misconfigured value fails wherever it is first used
    /// (<see cref="Uri"/> construction, store connection, first ingest), not with a diagnostic
    /// naming the setting and the configuration key that fixes it. Layering that validation on
    /// top of this wiring is follow-on work in the same phase.
    /// </remarks>
    public static IServiceCollection AddRagNetPipelineFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new RagNetOptions();
        configuration.GetSection(RagNetOptions.SectionName).Bind(options);

        services.AddChatClient(BuildChatClient(options.ChatClient));
        services.AddEmbeddingGenerator(BuildEmbeddingGenerator(options.Embeddings));
        services.AddRagNet(rag =>
            RegisterVectorStore(rag, options.VectorStore, options.Embeddings.VectorDimensions));

        return services;
    }

    private static IChatClient BuildChatClient(ChatClientOptions options) =>
        new OpenAIClient(BuildCredential(options.ApiKey), BuildClientOptions(options.Endpoint))
            .GetChatClient(options.Model)
            .AsIChatClient();

    private static IEmbeddingGenerator<string, Embedding<float>> BuildEmbeddingGenerator(
        EmbeddingsOptions options) =>
        new OpenAIClient(BuildCredential(options.ApiKey), BuildClientOptions(options.Endpoint))
            .GetEmbeddingClient(options.Model)
            .AsIEmbeddingGenerator();

    /// <summary>
    /// Providers reachable through this package's bounded OpenAI-compatible client do not all
    /// require a real key — a local Ollama or LM Studio instance accepts any non-empty value, but
    /// <see cref="ApiKeyCredential"/> itself rejects a null or empty one. An unset key is given a
    /// placeholder rather than failing before the setting even gets a chance to matter.
    /// </summary>
    private static ApiKeyCredential BuildCredential(string apiKey) =>
        new(string.IsNullOrEmpty(apiKey) ? "not-required" : apiKey);

    private static OpenAIClientOptions BuildClientOptions(string endpoint) =>
        new() { Endpoint = new Uri(endpoint, UriKind.Absolute) };

    /// <summary>
    /// Registers the configured <see cref="IVectorStore"/> kind. Each kind takes different
    /// settings because the builder extensions it wraps do — <c>UseQdrant</c> takes a host, port,
    /// and collection name; <c>UsePgVector</c> takes a connection string — so only the section
    /// matching <paramref name="options"/>'s <see cref="VectorStoreOptions.Kind"/> is read.
    /// Anything other than <c>Qdrant</c> or <c>PgVector</c>, including an empty or unrecognised
    /// value, resolves <c>InMemory</c>: the documented default, made loud by a startup warning
    /// this same phase adds separately.
    /// </summary>
    private static void RegisterVectorStore(
        RagBuilder rag, VectorStoreOptions options, int vectorDimensions)
    {
        if (string.Equals(options.Kind, "Qdrant", StringComparison.OrdinalIgnoreCase))
        {
            rag.UseQdrant(
                options.Qdrant.Host, options.Qdrant.Port, options.Qdrant.CollectionName,
                vectorDimensions);
        }
        else if (string.Equals(options.Kind, "PgVector", StringComparison.OrdinalIgnoreCase))
        {
            rag.UsePgVector(options.PgVector.ConnectionString, vectorDimensions);
        }
        else
        {
            rag.Services.AddSingleton<IVectorStore>(new InMemoryVectorStore());
        }
    }
}

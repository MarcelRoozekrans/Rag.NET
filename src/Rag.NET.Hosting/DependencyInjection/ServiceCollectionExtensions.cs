using System.ClientModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Hosting.Configuration;
using Rag.NET.Hosting.Logging;
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
    /// <exception cref="InvalidOperationException">
    /// The bound configuration is invalid — an unrecognised <c>RagNet:VectorStore:Kind</c>, a
    /// missing setting for the chosen kind, a missing chat or embeddings endpoint/model, a missing
    /// API key for a non-local endpoint, or an absent/non-positive
    /// <c>RagNet:Embeddings:VectorDimensions</c>. The message names every problem found, each
    /// naming both the setting and the configuration key that fixes it, so this fails while the
    /// host is being built rather than the first time an MCP tool is invoked.
    /// </exception>
    /// <remarks>
    /// What this validation cannot catch: whether <c>RagNet:Embeddings:VectorDimensions</c> is the
    /// number the endpoint actually returns. That is only knowable by embedding something, which
    /// startup must not do. A genuine mismatch — the value is positive and configured, but wrong
    /// for the model — surfaces as a vector-store failure at first ingest, not here.
    /// <para>
    /// When <c>RagNet:VectorStore:Kind</c> resolves to <c>InMemory</c> — set explicitly or left
    /// unset, both of which default to it — resolving <see cref="Rag.NET.Abstractions.IVectorStore"/>
    /// logs a warning naming the consequence (every ingested document is lost when the process
    /// exits) and the fix (<c>Qdrant</c> or <c>PgVector</c>). See <see cref="BuildDefaultInMemoryVectorStore"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRagNetPipelineFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new RagNetOptions();
        configuration.GetSection(RagNetOptions.SectionName).Bind(options);

        Validate(options);

        services.AddChatClient(BuildChatClient(options.ChatClient));
        services.AddEmbeddingGenerator(BuildEmbeddingGenerator(options.Embeddings));
        services.AddRagNet(rag =>
            RegisterVectorStore(rag, options.VectorStore, options.Embeddings.VectorDimensions));

        return services;
    }

    /// <summary>
    /// Runs every startup check and throws once with all of them, rather than stopping at the
    /// first — so fixing the configuration is not a one-error-at-a-time loop.
    /// </summary>
    private static void Validate(RagNetOptions options)
    {
        var errors = new List<string>();

        ValidateEndpointCredentialAndModel(
            "RagNet:ChatClient", options.ChatClient.Endpoint, options.ChatClient.ApiKey,
            options.ChatClient.Model, errors);
        ValidateEndpointCredentialAndModel(
            "RagNet:Embeddings", options.Embeddings.Endpoint, options.Embeddings.ApiKey,
            options.Embeddings.Model, errors);
        ValidateVectorDimensions(options.Embeddings.VectorDimensions, errors);
        ValidateVectorStore(options.VectorStore, errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(DescribeConfigurationErrors(errors));
        }
    }

    /// <summary>
    /// Checks the shape shared by <c>RagNet:ChatClient</c> and <c>RagNet:Embeddings</c>: a
    /// non-empty, absolute-URL endpoint; a model id; and an API key that may be omitted only when
    /// the endpoint is a loopback address (<c>localhost</c>, <c>127.0.0.1</c>, <c>[::1]</c>) —
    /// the shape a local Ollama or LM Studio instance has. Anything else needs a real key, or the
    /// missing credential surfaces as a confusing 401 from the provider instead of a clear
    /// startup message.
    /// </summary>
    private static void ValidateEndpointCredentialAndModel(
        string sectionPrefix, string endpoint, string apiKey, string model, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            errors.Add(
                $"{sectionPrefix}:Endpoint is required. Set it to the OpenAI-compatible base " +
                "URL, e.g. https://api.openai.com/v1, or http://localhost:11434/v1 for a " +
                "local Ollama instance.");
        }
        else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            errors.Add(
                $"{sectionPrefix}:Endpoint ('{endpoint}') is not a valid absolute URL.");
        }
        else if (string.IsNullOrEmpty(apiKey) && !uri.IsLoopback)
        {
            errors.Add(
                $"{sectionPrefix}:ApiKey is required because {sectionPrefix}:Endpoint " +
                $"('{endpoint}') is not a local address. Only loopback endpoints " +
                "(localhost, 127.0.0.1, [::1]) may leave it unset.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            errors.Add($"{sectionPrefix}:Model is required. Set it to the model id to request.");
        }
    }

    /// <summary>
    /// Refuses an absent or non-positive <c>RagNet:Embeddings:VectorDimensions</c>. It cannot
    /// refuse a wrong-but-positive one — see this class's <c>AddRagNetPipelineFromConfiguration</c>
    /// remarks for why.
    /// </summary>
    private static void ValidateVectorDimensions(int vectorDimensions, List<string> errors)
    {
        if (vectorDimensions <= 0)
        {
            errors.Add(
                "RagNet:Embeddings:VectorDimensions is required and must be positive. Set it " +
                "to the number of dimensions your embedding model produces, e.g. 1536 for " +
                "OpenAI's text-embedding-3-small or 768 for nomic-embed-text. It is currently " +
                vectorDimensions.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    /// <summary>
    /// Checks <c>RagNet:VectorStore:Kind</c> itself, then the settings the chosen kind needs.
    /// An empty or unset <c>Kind</c> means <c>InMemory</c> by default and is not an error; any
    /// non-empty value that is not one of the three recognised kinds is — a misspelling such as
    /// <c>"Qdrnat"</c> must not silently resolve to <c>InMemory</c>.
    /// </summary>
    private static void ValidateVectorStore(VectorStoreOptions options, List<string> errors)
    {
        if (string.IsNullOrEmpty(options.Kind) ||
            string.Equals(options.Kind, "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(options.Kind, "Qdrant", StringComparison.OrdinalIgnoreCase))
        {
            ValidateQdrant(options.Qdrant, errors);
        }
        else if (string.Equals(options.Kind, "PgVector", StringComparison.OrdinalIgnoreCase))
        {
            ValidatePgVector(options.PgVector, errors);
        }
        else
        {
            errors.Add(
                $"RagNet:VectorStore:Kind ('{options.Kind}') is not recognised. Valid values " +
                "are InMemory, Qdrant, or PgVector.");
        }
    }

    private static void ValidateQdrant(QdrantVectorStoreOptions options, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            errors.Add(
                "RagNet:VectorStore:Qdrant:Host is required when RagNet:VectorStore:Kind is " +
                "Qdrant. Set it to the Qdrant server's hostname, e.g. localhost.");
        }

        if (string.IsNullOrWhiteSpace(options.CollectionName))
        {
            errors.Add(
                "RagNet:VectorStore:Qdrant:CollectionName is required when " +
                "RagNet:VectorStore:Kind is Qdrant. Set it to the collection to store " +
                "chunks in.");
        }
    }

    private static void ValidatePgVector(PgVectorStoreOptions options, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            errors.Add(
                "RagNet:VectorStore:PgVector:ConnectionString is required when " +
                "RagNet:VectorStore:Kind is PgVector. Set it to a PostgreSQL connection " +
                "string.");
        }
    }

    private static string DescribeConfigurationErrors(List<string> errors) =>
        "Rag.NET configuration is invalid (" +
        errors.Count.ToString(CultureInfo.InvariantCulture) +
        (errors.Count == 1 ? " problem" : " problems") + "):" + Environment.NewLine +
        string.Join(Environment.NewLine, errors.Select(error => "- " + error));

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
    /// <see cref="ApiKeyCredential"/> itself rejects a null or empty one. By the time this runs,
    /// <see cref="Validate"/> has already required a real key for any non-loopback endpoint, so an
    /// empty <paramref name="apiKey"/> here only ever means a local endpoint that does not need
    /// one — the placeholder stands in for that case, not for a forgotten key.
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
    /// By the time this runs, <see cref="Validate"/> has already rejected any <c>Kind</c> other
    /// than an empty/unset value, <c>InMemory</c>, <c>Qdrant</c>, or <c>PgVector</c>, so the final
    /// branch here is reached only by an explicit or defaulted <c>InMemory</c> — never by a
    /// misspelling of one of the other two.
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
            rag.Services.AddSingleton<IVectorStore>(BuildDefaultInMemoryVectorStore);
        }
    }

    /// <summary>
    /// Builds the <c>InMemory</c> default and warns while doing it. This method exists at
    /// registration time only as a factory delegate — <c>AddRagNetPipelineFromConfiguration</c>
    /// runs before the service provider is built, so there is no <see cref="ILogger"/> to log
    /// through yet. Deferring the warning into the singleton factory means it fires the moment
    /// something actually resolves <see cref="IVectorStore"/> (typically while the host starts),
    /// using whatever <see cref="ILoggerFactory"/> the host itself registered — the same
    /// resolve-from-<see cref="IServiceProvider"/>-at-first-use shape as
    /// <c>UseCostBudgeting</c>'s in-memory cost-ledger warning. Without a registered
    /// <see cref="ILogger{TCategoryName}"/> (for example, a bare <see cref="ServiceCollection"/>
    /// in a test with no logging configured), the warning is built and immediately discarded by
    /// <see cref="NullLogger{T}"/> rather than throwing.
    /// </summary>
    private static InMemoryVectorStore BuildDefaultInMemoryVectorStore(IServiceProvider services)
    {
        var logger = services.GetService<ILogger<InMemoryVectorStore>>()
            ?? NullLogger<InMemoryVectorStore>.Instance;
        HostingLog.InMemoryVectorStoreIsTheDefault(logger);
        return new InMemoryVectorStore();
    }
}

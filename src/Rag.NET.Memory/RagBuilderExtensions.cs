using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;

namespace Rag.NET.Memory;

/// <summary>Extension methods for registering persistent conversation memory in the Rag.NET pipeline.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Wraps the conversation memory pipeline with <see cref="PersistentConversationMemory"/>,
    /// which retrieves relevant past exchange pairs from the vector store and injects them as a
    /// system-message prefix before delegating to the inner pipeline.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="IVectorStore"/> and
    /// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> to be registered in DI.
    /// Call this inside the <c>configure</c> delegate of
    /// <see cref="RagBuilder.UseConversationMemory"/>.
    /// </remarks>
    public static ConversationMemoryBuilder UsePersistentMemory(
        this ConversationMemoryBuilder builder,
        PersistentMemoryOptions? options = null)
    {
        var persistentOpts = options ?? new PersistentMemoryOptions();
        builder.Services.AddSingleton(persistentOpts);
        builder.DecoratorFactory = (sp, pipeline) =>
            new PersistentConversationMemory(
                pipeline,
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                persistentOpts,
                sp.GetService<ILogger<PersistentConversationMemory>>());
        return builder;
    }
}

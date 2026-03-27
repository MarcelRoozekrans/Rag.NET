using Rag.NET.Models.Options;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Configures optional decorators over <see cref="Rag.NET.Memory.ConversationMemoryPipeline"/>.
/// Obtained via the <c>configure</c> parameter of <see cref="RagBuilder.UseConversationMemory"/>.
/// </summary>
public sealed class ConversationMemoryBuilder
{
    internal ConversationMemoryBuilder() { }

    private bool _usePersistentMemory;
    private PersistentMemoryOptions? _persistentMemoryOptions;

    internal bool HasPersistentMemory => _usePersistentMemory;
    internal PersistentMemoryOptions PersistentMemoryOptions =>
        _persistentMemoryOptions ??= new PersistentMemoryOptions();

    /// <summary>
    /// Wraps the conversation memory pipeline with
    /// <see cref="Rag.NET.Memory.PersistentConversationMemory"/>, which retrieves relevant past
    /// exchange pairs from the vector store and injects them as a system-message prefix before
    /// delegating to the inner pipeline.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="Rag.NET.Abstractions.IVectorStore"/> and
    /// <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> to be registered in DI.
    /// </remarks>
    public ConversationMemoryBuilder UsePersistentMemory(PersistentMemoryOptions? options = null)
    {
        _usePersistentMemory = true;
        _persistentMemoryOptions = options;
        return this;
    }
}

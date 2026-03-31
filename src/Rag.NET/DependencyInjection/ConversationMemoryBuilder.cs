using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Configures optional decorators over <see cref="Rag.NET.Memory.ConversationMemoryPipeline"/>.
/// Obtained via the <c>configure</c> parameter of <see cref="RagBuilder.UseConversationMemory"/>.
/// </summary>
public sealed class ConversationMemoryBuilder
{
    internal ConversationMemoryBuilder(IServiceCollection services) { Services = services; }

    /// <summary>Gets the underlying <see cref="IServiceCollection"/> for additional registrations.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Optional factory set by external packages (e.g. <c>Rag.NET.Memory</c>) to wrap the
    /// inner <see cref="Rag.NET.Abstractions.IConversationMemory"/> pipeline with a decorator.
    /// When set, <see cref="RagBuilder.UseConversationMemory"/> delegates final registration
    /// to this factory instead of registering the bare pipeline.
    /// </summary>
    public Func<IServiceProvider, Rag.NET.Abstractions.IConversationMemory, Rag.NET.Abstractions.IConversationMemory>? DecoratorFactory { get; set; }
}

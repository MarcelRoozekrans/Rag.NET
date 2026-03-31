using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;

namespace Rag.NET.Chunking.CSharp;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="CSharpChunkingStrategy"/> as <see cref="IChunkingStrategy"/>.
    /// Uses Roslyn to split C# source files at AST member boundaries (class, method, property, etc.),
    /// carrying structured metadata per chunk.
    /// </summary>
    public static TBuilder UseCSharpChunking<TBuilder>(this TBuilder builder, Action<CSharpChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new CSharpChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<IChunkingStrategy>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory is not null
                ? loggerFactory.CreateLogger<CSharpChunkingStrategy>()
                : (ILogger<CSharpChunkingStrategy>)NullLogger<CSharpChunkingStrategy>.Instance;
            return new CSharpChunkingStrategy(opts, logger);
        });
        return builder;
    }
}

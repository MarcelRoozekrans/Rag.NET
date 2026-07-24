using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class UseEmbeddingVersioningTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-embver-di-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void UseEmbeddingVersioning_RegistersSqliteStoreAndOptions()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseEmbeddingVersioning(o => o.DatabasePath = _dbPath))
            .BuildServiceProvider();

        Assert.IsType<SqliteEmbeddingVersionStore>(sp.GetRequiredService<IEmbeddingVersionStore>());
        Assert.Equal(_dbPath, sp.GetRequiredService<EmbeddingVersioningOptions>().DatabasePath);
    }

    [Fact]
    public void UseEmbeddingVersioning_ModelIdOverride_FlowsIntoOptions()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseEmbeddingVersioning(o =>
            {
                o.DatabasePath = _dbPath;
                o.ModelId = "my-model";
            }))
            .BuildServiceProvider();

        Assert.Equal("my-model", sp.GetRequiredService<EmbeddingVersioningOptions>().ModelId);
    }

    [Fact]
    public void UseEmbeddingVersioning_EmptyDatabasePath_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddRagNet(rag => rag.UseEmbeddingVersioning(o => o.DatabasePath = " ")));
    }

    [Fact]
    public void UseEmbeddingVersioning_CalledTwice_FirstRegistrationWins()
    {
        var otherPath = Path.Combine(Path.GetTempPath(), $"ragnet-embver-di2-{Guid.NewGuid():N}.db");
        try
        {
            var sp = new ServiceCollection()
                .AddRagNet(rag => rag
                    .UseEmbeddingVersioning(o => o.DatabasePath = _dbPath)
                    .UseEmbeddingVersioning(o => o.DatabasePath = otherPath))
                .BuildServiceProvider();

            Assert.Equal(_dbPath, sp.GetRequiredService<EmbeddingVersioningOptions>().DatabasePath);
        }
        finally
        {
            if (File.Exists(otherPath)) File.Delete(otherPath);
        }
    }
}

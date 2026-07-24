using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

public class UseOnnxTokenEmbeddingsTests
{
    [Fact]
    public void UseOnnxTokenEmbeddings_ValidPaths_RegistrationSucceeds_FileChecksHappenAtResolution()
    {
        // Non-empty but nonexistent paths: registration must succeed (only emptiness is
        // validated eagerly); the FileNotFoundException fires when the generator is RESOLVED,
        // matching the ONNX reranker's timing.
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseOnnxTokenEmbeddings(o =>
            {
                o.ModelPath = "nonexistent/model.onnx";
                o.TokenizerVocabPath = "nonexistent/vocab.txt";
            }))
            .BuildServiceProvider();

        Assert.Throws<FileNotFoundException>(() => sp.GetRequiredService<ITokenEmbeddingGenerator>());
    }

    [Fact]
    public void UseOnnxTokenEmbeddings_OptionsAreRegistered()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseOnnxTokenEmbeddings(o =>
            {
                o.ModelPath = "nonexistent/model.onnx";
                o.TokenizerVocabPath = "nonexistent/vocab.txt";
                o.MaxTokens = 512;
            }))
            .BuildServiceProvider();

        var options = sp.GetRequiredService<OnnxTokenEmbeddingOptions>();
        Assert.Equal("nonexistent/model.onnx", options.ModelPath);
        Assert.Equal(512, options.MaxTokens);
    }

    [Fact]
    public void UseOnnxTokenEmbeddings_EmptyModelPath_ThrowsArgumentExceptionAtRegistration()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseOnnxTokenEmbeddings(o =>
                o.TokenizerVocabPath = "nonexistent/vocab.txt")));

        Assert.Contains("ModelPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseOnnxTokenEmbeddings_EmptyVocabPath_ThrowsArgumentExceptionAtRegistration()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseOnnxTokenEmbeddings(o =>
                o.ModelPath = "nonexistent/model.onnx")));

        Assert.Contains("TokenizerVocabPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseOnnxTokenEmbeddings_NullConfigure_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseOnnxTokenEmbeddings(null!)));
    }
}

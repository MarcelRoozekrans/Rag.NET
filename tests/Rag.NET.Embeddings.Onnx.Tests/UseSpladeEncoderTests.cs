using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

public class UseSpladeEncoderTests
{
    [Fact]
    public void UseSpladeEncoder_ValidPaths_RegistrationSucceeds_FileChecksHappenAtResolution()
    {
        // Non-empty but nonexistent paths: registration must succeed (only emptiness is
        // validated eagerly); the FileNotFoundException fires when the encoder is RESOLVED,
        // matching UseOnnxTokenEmbeddings' timing.
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseSpladeEncoder(o =>
            {
                o.ModelPath = "nonexistent/model.onnx";
                o.TokenizerVocabPath = "nonexistent/vocab.txt";
            }))
            .BuildServiceProvider();

        Assert.Throws<FileNotFoundException>(() => sp.GetRequiredService<ISparseEmbeddingGenerator>());
    }

    [Fact]
    public void UseSpladeEncoder_OptionsAreRegistered()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseSpladeEncoder(o =>
            {
                o.ModelPath = "nonexistent/model.onnx";
                o.TokenizerVocabPath = "nonexistent/vocab.txt";
                o.TopTerms = 128;
            }))
            .BuildServiceProvider();

        var options = sp.GetRequiredService<OnnxSpladeOptions>();
        Assert.Equal("nonexistent/model.onnx", options.ModelPath);
        Assert.Equal(128, options.TopTerms);
        Assert.Equal(512, options.MaxTokens);
    }

    [Fact]
    public void UseSpladeEncoder_EmptyModelPath_ThrowsArgumentExceptionAtRegistration()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseSpladeEncoder(o =>
                o.TokenizerVocabPath = "nonexistent/vocab.txt")));

        Assert.Contains("ModelPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseSpladeEncoder_EmptyVocabPath_ThrowsArgumentExceptionAtRegistration()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseSpladeEncoder(o =>
                o.ModelPath = "nonexistent/model.onnx")));

        Assert.Contains("TokenizerVocabPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseSpladeEncoder_NullConfigure_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseSpladeEncoder(null!)));
    }
}

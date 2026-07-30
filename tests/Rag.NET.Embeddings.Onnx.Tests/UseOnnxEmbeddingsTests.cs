using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

public class UseOnnxEmbeddingsTests
{
    [Fact]
    public void UseOnnxEmbeddings_ValidPaths_RegistrationSucceeds_FileChecksHappenAtResolution()
    {
        // Non-empty but nonexistent paths: registration must succeed (only emptiness is validated
        // eagerly); the FileNotFoundException fires when the generator is RESOLVED, matching
        // UseOnnxTokenEmbeddings' timing.
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseOnnxEmbeddings(o =>
            {
                o.ModelPath = "nonexistent/model.onnx";
                o.TokenizerVocabPath = "nonexistent/vocab.txt";
            }))
            .BuildServiceProvider();

        Assert.Throws<FileNotFoundException>(
            () => sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void UseOnnxEmbeddings_OptionsAreRegistered()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseOnnxEmbeddings(o =>
            {
                o.ModelPath = "nonexistent/model.onnx";
                o.TokenizerVocabPath = "nonexistent/vocab.txt";
                o.MaxTokens = 512;
                o.BatchSize = 4;
                o.ModelId = "all-MiniLM-L6-v2";
            }))
            .BuildServiceProvider();

        var options = sp.GetRequiredService<OnnxEmbeddingOptions>();
        Assert.Equal("nonexistent/model.onnx", options.ModelPath);
        Assert.Equal(512, options.MaxTokens);
        Assert.Equal(4, options.BatchSize);
        Assert.Equal("all-MiniLM-L6-v2", options.ModelId);
    }

    [Fact]
    public void UseOnnxEmbeddings_Defaults_MatchTheModelTheParityFiguresComeFrom()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseOnnxEmbeddings(o =>
            {
                o.ModelPath = "nonexistent/model.onnx";
                o.TokenizerVocabPath = "nonexistent/vocab.txt";
            }))
            .BuildServiceProvider();

        var options = sp.GetRequiredService<OnnxEmbeddingOptions>();

        // all-MiniLM-L6-v2's max_seq_length; changing this shifts every retrieval number.
        Assert.Equal(256, options.MaxTokens);
        Assert.Equal("last_hidden_state", options.OutputName);
    }

    [Fact]
    public void UseOnnxEmbeddings_EmptyModelPath_ThrowsArgumentExceptionAtRegistration()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseOnnxEmbeddings(o =>
                o.TokenizerVocabPath = "nonexistent/vocab.txt")));

        Assert.Contains("ModelPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseOnnxEmbeddings_EmptyVocabPath_ThrowsArgumentExceptionAtRegistration()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseOnnxEmbeddings(o =>
                o.ModelPath = "nonexistent/model.onnx")));

        Assert.Contains("TokenizerVocabPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseOnnxEmbeddings_NullConfigure_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseOnnxEmbeddings(null!)));
    }
}

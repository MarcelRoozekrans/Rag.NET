using Xunit;

namespace Rag.NET.Reranking.Onnx.Tests;

public class OnnxRerankerTests
{
    [Fact]
    public void Constructor_WhenModelPathDoesNotExist_ThrowsFileNotFoundException()
    {
        var options = new OnnxRerankerOptions
        {
            ModelPath = "nonexistent/model.onnx",
        };

        Assert.Throws<FileNotFoundException>(() => new OnnxReranker(options));
    }

    [Fact]
    public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OnnxReranker(null!));
    }
}

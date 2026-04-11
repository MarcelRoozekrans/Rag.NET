using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class RagErrorTests
{
    [Fact]
    public void ValidationFailed_HoldsFailures()
    {
        var failures = new[] { new ValidationFailure("TopK", "must be > 0") };
        var error = new RagError.ValidationFailed(failures);
        Assert.Single(error.Failures);
        Assert.Equal("TopK", error.Failures[0].PropertyName);
    }

    [Fact]
    public void NoParserFound_HoldsContentType()
    {
        var error = new RagError.NoParserFound("application/pdf");
        Assert.Equal("application/pdf", error.ContentType);
    }

    [Fact]
    public void StorageFailed_HoldsException()
    {
        var ex = new InvalidOperationException("db down");
        var error = new RagError.StorageFailed(ex);
        Assert.Same(ex, error.Inner);
    }

    [Fact]
    public void NonSeekableStream_IsDistinctSubtype()
    {
        RagError error = new RagError.NonSeekableStream();
        Assert.IsType<RagError.NonSeekableStream>(error);
    }

    [Fact]
    public void RagError_HttpFailed_CanBeConstructedAndPatternMatched()
    {
        RagError error = new RagError.HttpFailed(System.Net.HttpStatusCode.NotFound, "Not Found");

        var message = error switch
        {
            RagError.HttpFailed { StatusCode: System.Net.HttpStatusCode.NotFound } e => $"HTTP {(int)e.StatusCode}",
            _ => "other",
        };

        Assert.Equal("HTTP 404", message);
    }
}

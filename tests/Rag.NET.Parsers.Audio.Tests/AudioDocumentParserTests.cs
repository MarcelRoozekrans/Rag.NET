using Whisper.net.Ggml;
using Xunit;

namespace Rag.NET.Parsers.Audio.Tests;

public class AudioDocumentParserTests
{
    [Fact]
    public void AudioParserOptions_Defaults_AreCorrect()
    {
        var opts = new AudioParserOptions();
        Assert.Equal(GgmlType.Base, opts.ModelType);
        Assert.Null(opts.Language);
        Assert.Equal(Path.GetTempPath(), opts.ModelCacheDirectory);
    }

    [Theory]
    [InlineData("audio/wav",  true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("audio/flac", true)]
    [InlineData("audio/ogg",  true)]
    [InlineData("audio/mp4",  true)]
    [InlineData("application/pdf",  false)]
    [InlineData("text/plain",       false)]
    [InlineData("application/json", false)]
    public void CanParse_VariousContentTypes_ReturnsExpected(string contentType, bool expected)
    {
        var sut = new AudioDocumentParser(new AudioParserOptions());
        Assert.Equal(expected, sut.CanParse(contentType));
    }
}

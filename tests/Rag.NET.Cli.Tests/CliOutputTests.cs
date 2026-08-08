using Xunit;

namespace Rag.NET.Cli.Tests;

/// <summary>Covers <see cref="CliOutput"/>'s command recognition and JSON/usage formatting.</summary>
public sealed class CliOutputTests
{
    [Theory]
    [InlineData("ingest")]
    [InlineData("QUERY")]
    [InlineData("Evaluate")]
    public void IsKnownCommand_RecognisedCommand_ReturnsTrueRegardlessOfCase(string command)
    {
        Assert.True(CliOutput.IsKnownCommand(command));
    }

    [Fact]
    public void IsKnownCommand_UnrecognisedCommand_ReturnsFalse()
    {
        Assert.False(CliOutput.IsKnownCommand("delete"));
    }

    [Fact]
    public void WriteJson_WritesTheValueAsIndentedJson()
    {
        using var writer = new StringWriter();

        CliOutput.WriteJson(writer, new { Answer = 42 });

        var text = writer.ToString();
        Assert.Contains("\"Answer\"", text, StringComparison.Ordinal);
        Assert.Contains("42", text, StringComparison.Ordinal);
        Assert.Contains('\n', text);
    }

    [Fact]
    public void WriteUsage_MentionsEveryImplementedCommand()
    {
        using var writer = new StringWriter();

        CliOutput.WriteUsage(writer);

        var text = writer.ToString();
        Assert.Contains("ragnet ingest", text, StringComparison.Ordinal);
        Assert.Contains("ragnet query", text, StringComparison.Ordinal);
    }
}

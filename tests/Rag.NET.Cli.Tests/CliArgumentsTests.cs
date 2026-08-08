using Xunit;

namespace Rag.NET.Cli.Tests;

/// <summary>
/// Covers <see cref="CliArguments.Parse"/>: the command, the positional argument, and the
/// <c>--top-k</c>/<c>--overwrite</c>/<c>--help</c> options it recognises.
/// </summary>
public sealed class CliArgumentsTests
{
    [Fact]
    public void Parse_EmptyArgs_SetsShowHelp()
    {
        var parsed = CliArguments.Parse([]);

        Assert.True(parsed.ShowHelp);
        Assert.Null(parsed.Command);
    }

    [Fact]
    public void Parse_HelpFlagFirst_SetsShowHelpAndNoCommand()
    {
        var parsed = CliArguments.Parse(["--help"]);

        Assert.True(parsed.ShowHelp);
        Assert.Null(parsed.Command);
    }

    [Fact]
    public void Parse_ShortHelpFlagFirst_SetsShowHelp()
    {
        var parsed = CliArguments.Parse(["-h"]);

        Assert.True(parsed.ShowHelp);
    }

    [Fact]
    public void Parse_CommandOnly_HasNoArgument()
    {
        var parsed = CliArguments.Parse(["ingest"]);

        Assert.Equal("ingest", parsed.Command);
        Assert.Null(parsed.Argument);
        Assert.False(parsed.ShowHelp);
    }

    [Fact]
    public void Parse_CommandWithSingleToken_ReturnsThatTokenAsArgument()
    {
        var parsed = CliArguments.Parse(["ingest", "file.txt"]);

        Assert.Equal("file.txt", parsed.Argument);
    }

    [Fact]
    public void Parse_QueryWithMultipleWords_JoinsThemWithASpace()
    {
        var parsed = CliArguments.Parse(["query", "What", "is", "RAG"]);

        Assert.Equal("What is RAG", parsed.Argument);
    }

    [Fact]
    public void Parse_TopKOption_SetsTheParsedValue()
    {
        var parsed = CliArguments.Parse(["query", "question", "--top-k", "10"]);

        Assert.Equal(10, parsed.TopK);
        Assert.Equal("question", parsed.Argument);
    }

    [Fact]
    public void Parse_TopKOption_IsCaseInsensitive()
    {
        var parsed = CliArguments.Parse(["query", "question", "--TOP-K", "3"]);

        Assert.Equal(3, parsed.TopK);
    }

    [Fact]
    public void Parse_NoTopKOption_DefaultsToFive()
    {
        var parsed = CliArguments.Parse(["query", "question"]);

        Assert.Equal(5, parsed.TopK);
    }

    [Fact]
    public void Parse_TopKIsTheLastToken_FallsBackToDefaultAndKeepsTheFlagAsPositional()
    {
        // A bare "--top-k" with nothing after it has no value to consume, so it is treated like
        // any other unrecognised token: added to the positional argument rather than silently
        // discarded.
        var parsed = CliArguments.Parse(["query", "question", "--top-k"]);

        Assert.Equal(5, parsed.TopK);
        Assert.Equal("question --top-k", parsed.Argument);
    }

    [Fact]
    public void Parse_TopKValueIsNotAnInteger_FallsBackToDefaultAndKeepsBothTokensAsPositional()
    {
        var parsed = CliArguments.Parse(["query", "question", "--top-k", "not-a-number"]);

        Assert.Equal(5, parsed.TopK);
        Assert.Equal("question --top-k not-a-number", parsed.Argument);
    }

    [Fact]
    public void Parse_OverwriteFlag_SetsOverwriteTrue()
    {
        var parsed = CliArguments.Parse(["ingest", "./docs", "--overwrite"]);

        Assert.True(parsed.Overwrite);
        Assert.Equal("./docs", parsed.Argument);
    }

    [Fact]
    public void Parse_NoOverwriteFlag_DefaultsToFalse()
    {
        var parsed = CliArguments.Parse(["ingest", "./docs"]);

        Assert.False(parsed.Overwrite);
    }

    [Fact]
    public void Parse_HelpFlagAfterCommand_SetsShowHelpButKeepsTheCommand()
    {
        var parsed = CliArguments.Parse(["ingest", "--help"]);

        Assert.True(parsed.ShowHelp);
        Assert.Equal("ingest", parsed.Command);
    }

    [Fact]
    public void Parse_UnrecognisedFlag_IsTreatedAsPositionalText()
    {
        var parsed = CliArguments.Parse(["query", "--verbose", "question"]);

        Assert.Equal("--verbose question", parsed.Argument);
    }
}

using Rag.NET.Mcp.Tool;
using Xunit;

namespace Rag.NET.Mcp.Tool.Tests;

/// <summary>
/// Covers the argument parsing <c>Program.cs</c> delegates to <see cref="ProgramArguments"/>:
/// both the <c>--key value</c> and <c>--key=value</c> forms it accepts for
/// <c>--transport</c>, <c>--port</c>, and <c>--api-key</c>.
/// </summary>
public sealed class ProgramArgumentsTests
{
    [Fact]
    public void Parse_SpaceSeparatedForm_ReturnsTheFollowingToken()
    {
        var value = ProgramArguments.Parse(["--transport", "http"], "--transport");

        Assert.Equal("http", value);
    }

    [Fact]
    public void Parse_EqualsSeparatedForm_ReturnsTheValueAfterTheEquals()
    {
        var value = ProgramArguments.Parse(["--transport=http"], "--transport");

        Assert.Equal("http", value);
    }

    [Fact]
    public void Parse_FlagNameIsCaseInsensitive_SpaceSeparatedForm()
    {
        var value = ProgramArguments.Parse(["--TRANSPORT", "http"], "--transport");

        Assert.Equal("http", value);
    }

    [Fact]
    public void Parse_FlagNameIsCaseInsensitive_EqualsSeparatedForm()
    {
        var value = ProgramArguments.Parse(["--Api-Key=secret"], "--api-key");

        Assert.Equal("secret", value);
    }

    [Fact]
    public void Parse_FlagAbsent_ReturnsNull()
    {
        var value = ProgramArguments.Parse(["--transport", "http"], "--port");

        Assert.Null(value);
    }

    [Fact]
    public void Parse_EmptyArgumentArray_ReturnsNull()
    {
        var value = ProgramArguments.Parse([], "--transport");

        Assert.Null(value);
    }

    [Fact]
    public void Parse_SpaceSeparatedFlagIsTheLastToken_ReturnsNull()
    {
        // A bare "--port" with nothing after it has no value to return — this is the case that
        // used to leave callers falling back to their own default.
        var value = ProgramArguments.Parse(["--transport", "http", "--port"], "--port");

        Assert.Null(value);
    }

    [Fact]
    public void Parse_EqualsSeparatedFlagWithEmptyValue_ReturnsEmptyString()
    {
        var value = ProgramArguments.Parse(["--port="], "--port");

        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void Parse_DoesNotMatchAFlagNameThatIsOnlyAPrefix()
    {
        // "--porta" must not satisfy a lookup for "--port": the space-separated branch requires
        // an exact token match, and the equals-separated branch requires the literal "--port=".
        var value = ProgramArguments.Parse(["--porta", "9999"], "--port");

        Assert.Null(value);
    }

    [Fact]
    public void Parse_MultipleFlagsPresent_ReturnsTheRequestedOnesValue()
    {
        var args = new[] { "--transport", "http", "--port", "9090", "--api-key", "secret" };

        Assert.Equal("http", ProgramArguments.Parse(args, "--transport"));
        Assert.Equal("9090", ProgramArguments.Parse(args, "--port"));
        Assert.Equal("secret", ProgramArguments.Parse(args, "--api-key"));
    }

    [Fact]
    public void Parse_FlagRepeated_ReturnsTheFirstOccurrence()
    {
        var value = ProgramArguments.Parse(["--transport", "http", "--transport", "stdio"], "--transport");

        Assert.Equal("http", value);
    }
}

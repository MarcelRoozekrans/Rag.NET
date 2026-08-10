using Xunit;

namespace Rag.NET.Tests.Serialization;

/// <summary>
/// Pins <see cref="LlmJsonExtractor"/> against the response shapes chat models actually return.
/// The preamble-then-fence case is the shape that left GraphRAG extracting zero entities for the
/// life of the package while its unit tests, fed pre-stripped JSON, stayed green.
/// </summary>
public class LlmJsonExtractorTests
{
    private const string Payload = """{"entities":[{"name":"Marie Curie"}]}""";
    private const string ArrayPayload = """["one","two"]""";

    public static TheoryData<string, string> ObjectShapes() => new()
    {
        { "bare json", Payload },
        { "unlabelled fence", $"```\n{Payload}\n```" },
        { "labelled fence", $"```json\n{Payload}\n```" },
        { "preamble then fence", $"Here is the extracted information in JSON format:\n\n```\n{Payload}\n```" },
        { "preamble then labelled fence", $"Sure! Here you go:\n\n```json\n{Payload}\n```" },
        { "preamble then bare json", $"Here is the JSON:\n\n{Payload}" },
        { "fence then trailing prose", $"```\n{Payload}\n```\n\nLet me know if you need anything else." },
        { "bare json then trailing prose without braces", $"{Payload}\nLet me know if you need anything else." },
        { "unterminated fence", $"```json\n{Payload}" },
    };

    [Theory]
    [MemberData(nameof(ObjectShapes))]
    public void Extract_Object_FindsThePayloadWhateverWrapsIt(string shape, string response)
    {
        var extracted = LlmJsonExtractor.Extract(response, LlmJsonPayloadKind.Object);

        Assert.True(
            string.Equals(Payload, extracted, StringComparison.Ordinal),
            $"The '{shape}' response did not yield the payload; got: {extracted}");
    }

    public static TheoryData<string, string> ArrayShapes() => new()
    {
        { "bare array", ArrayPayload },
        { "labelled fence", $"```json\n{ArrayPayload}\n```" },
        { "preamble then fence", $"Here are the propositions:\n\n```\n{ArrayPayload}\n```" },
        { "preamble then bare array", $"Here are the propositions:\n\n{ArrayPayload}" },
        { "fence then trailing prose", $"```\n{ArrayPayload}\n```\nHope this helps!" },
    };

    [Theory]
    [MemberData(nameof(ArrayShapes))]
    public void Extract_Array_FindsThePayloadWhateverWrapsIt(string shape, string response)
    {
        var extracted = LlmJsonExtractor.Extract(response, LlmJsonPayloadKind.Array);

        Assert.True(
            string.Equals(ArrayPayload, extracted, StringComparison.Ordinal),
            $"The '{shape}' response did not yield the payload; got: {extracted}");
    }

    [Fact]
    public void Extract_BracesInsideStringValues_DoNotTruncateThePayload()
    {
        // A naive first-{-to-last-} slice survives this one, but a naive depth counter that is
        // not string-aware would close early on the brace inside the value. The scan must treat
        // string contents as opaque.
        const string payload = """{"code":"if (x) { return \"}\"; }","ok":true}""";

        var extracted = LlmJsonExtractor.Extract(
            $"Here you go:\n{payload}", LlmJsonPayloadKind.Object);

        Assert.Equal(payload, extracted);
    }

    [Fact]
    public void Extract_DecoyObjectInPreamble_LosesToTheRealPayloadOnLength()
    {
        // The prompt itself says 'Return {} if nothing applies', and models echo instructions.
        // First-candidate-wins would return the decoy — an empty result indistinguishable from
        // a legitimately empty answer, which is the failure mode this helper exists to remove.
        var response = "As instructed, I would return {} if nothing applied, but here it is:\n" + Payload;

        var extracted = LlmJsonExtractor.Extract(response, LlmJsonPayloadKind.Object);

        Assert.Equal(Payload, extracted);
    }

    [Fact]
    public void Extract_ProseBracketsBeforeAnArray_AreSkippedAsInvalidJson()
    {
        var response = $"See sources [sic] and [note 1]:\n{ArrayPayload}";

        var extracted = LlmJsonExtractor.Extract(response, LlmJsonPayloadKind.Array);

        Assert.Equal(ArrayPayload, extracted);
    }

    [Fact]
    public void Extract_FenceBodyThatIsNotJson_FallsBackToTheBareScan()
    {
        var response = $"```python\nprint('hi')\n```\nThe result: {Payload}";

        var extracted = LlmJsonExtractor.Extract(response, LlmJsonPayloadKind.Object);

        Assert.Equal(Payload, extracted);
    }

    [Theory]
    [InlineData("0.42", "0.42")]
    [InlineData("```json\n0.42\n```", "0.42")]
    [InlineData("The answer is:\n```\n0.42\n```\nGood luck!", "0.42")]
    [InlineData("The score is 0.42", "The score is 0.42")]
    public void Extract_FencedOnly_HonoursFencesButNeverSalvages(string response, string expected)
    {
        // FencedOnly exists for two callers: RagasJudge, which deliberately refuses to score a
        // reply the model did not clearly mark as JSON, and the confidence scorer, whose payload
        // is a bare number with no brackets to scan for.
        var extracted = LlmJsonExtractor.Extract(response, LlmJsonPayloadKind.FencedOnly);

        Assert.Equal(expected, extracted);
    }

    [Theory]
    [InlineData("I'm sorry, I can't do that.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Extract_NoJsonAnywhere_ReturnsTheTrimmedResponse(string response)
    {
        // The caller's own failure path (throw, log, fall back) must fire exactly as before.
        var extracted = LlmJsonExtractor.Extract(response, LlmJsonPayloadKind.Object);

        Assert.Equal(response.Trim(), extracted);
    }
}

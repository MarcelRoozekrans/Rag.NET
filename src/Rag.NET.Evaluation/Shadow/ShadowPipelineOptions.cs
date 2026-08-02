namespace Rag.NET.Evaluation.Shadow;

/// <summary>Naming for the two sides of every pair a <see cref="ShadowRagPipeline"/> captures.</summary>
/// <remarks>
/// The offline scorer keys everything by variant name, so both labels must be non-blank and the
/// two must differ. Blankness is refused here, where the option is set; distinctness is refused
/// by the decorator's constructor, which is the first place that sees both.
/// </remarks>
public sealed class ShadowPipelineOptions
{
    /// <summary>The label for the side the caller is served. Default <c>"primary"</c>.</summary>
    /// <exception cref="ArgumentException">The value is blank.</exception>
    public string PrimaryVariantName
    {
        get => _primaryVariantName;
        init => _primaryVariantName = RequireName(value, nameof(PrimaryVariantName));
    }

    /// <summary>The label for the shadowed side. Default <c>"shadow"</c>.</summary>
    /// <exception cref="ArgumentException">The value is blank.</exception>
    public string SecondaryVariantName
    {
        get => _secondaryVariantName;
        init => _secondaryVariantName = RequireName(value, nameof(SecondaryVariantName));
    }

    private readonly string _primaryVariantName = "primary";
    private readonly string _secondaryVariantName = "shadow";

    private static string RequireName(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{propertyName} must be non-blank; the offline scorer keys everything by variant name.",
                nameof(value));
        }

        return value;
    }
}

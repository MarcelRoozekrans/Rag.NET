using System.Runtime.InteropServices;

namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>How much of one section's candidates made it into the context, and at what token cost.</summary>
/// <param name="Rendered">Rows written.</param>
/// <param name="Offered">Rows that were candidates.</param>
/// <param name="Tokens">Tokens the rendered rows and the header cost.</param>
/// <param name="Budget">Tokens this section was allowed.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct SectionFill(int Rendered, int Offered, int Tokens, int Budget)
{
    /// <summary>Whether the section had candidates it could not fit.</summary>
    public bool Truncated => Rendered < Offered;
}

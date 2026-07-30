namespace Rag.NET.Parsers.Email;

/// <summary>
/// Decides whether <see cref="EmbeddedTraversal"/> may descend into an embedded message, and
/// under what context.
/// </summary>
/// <remarks>
/// Traversal and limit policy are different jobs, and separating them is also the only reason the
/// driver's central claim is testable: <see cref="EmailParserOptions.MaxSupportedEmbeddedDepth"/>
/// clamps every route through the public surface to 64 levels, an order of magnitude below the
/// depth at which the traversal this replaced overflowed. A test wiring an always-yes policy
/// reaches a depth no fixture can.
/// </remarks>
internal interface IDescentPolicy
{
    /// <summary>
    /// The context the child is parsed under, or <see langword="null"/> to skip the branch —
    /// in which case the driver continues with the next sibling.
    /// </summary>
    ContainerContext? TryDescend(ContainerContext parent, string name);
}

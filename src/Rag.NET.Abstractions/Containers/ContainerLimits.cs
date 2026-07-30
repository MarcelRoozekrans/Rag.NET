namespace Rag.NET;

/// <summary>
/// The nesting bounds a container parse runs under, in terms neutral to the container format.
/// </summary>
/// <param name="MaxNestingDepth">
/// How deep nested containers may go. <c>0</c> disables descent entirely, and does so
/// <i>silently</i> — recursion turned off deliberately should not produce a warning per child.
/// </param>
/// <param name="MaxEntries">
/// How many nested containers may be entered across the whole document, as a total rather than
/// per branch. See <see cref="ContainerBudget"/> for why the distinction is load-bearing.
/// </param>
/// <remarks>
/// This exists so <see cref="ContainerContext"/> need not know about any particular parser's
/// options type. Before Phase 3.10 the context held an <c>EmailParserOptions</c> directly, which
/// made it unusable by any other container format — and the archive parser needs the same depth and
/// entry accounting, sharing one budget with the email parsers rather than keeping its own.
/// </remarks>
public sealed record ContainerLimits(int MaxNestingDepth, int MaxEntries);

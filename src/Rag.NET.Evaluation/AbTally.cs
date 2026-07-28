namespace Rag.NET.Evaluation;

/// <summary>How often each variant won a paired comparison, and how often they tied.</summary>
/// <remarks>
/// The tally answers "how consistently" where the mean delta answers "by how much". They can
/// disagree — one enormous win carries a mean that eight losses out of ten contradict — and reading
/// them together is what stops a single outlying sample being reported as a verdict.
/// </remarks>
/// <param name="BWins">Pairs where B scored higher than A by more than the tie epsilon.</param>
/// <param name="AWins">Pairs where A scored higher than B by more than the tie epsilon.</param>
/// <param name="Ties">Pairs whose difference fell inside the tie epsilon, in either direction.</param>
public sealed record AbTally(int BWins, int AWins, int Ties);

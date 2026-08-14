using Rag.NET.Abstractions;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// The answer engine the pipeline uses: the registered <see cref="IAnswerEngine"/> (or the
/// <c>ChatAnswerEngine</c> built from a registered <c>IChatClient</c> when none is registered),
/// wrapped in everything added to the <see cref="AnswerEngineDecorationBuilder"/>.
/// </summary>
/// <remarks>
/// Resolving <see cref="IAnswerEngine"/> yields the engine that was <i>registered</i> and nothing
/// more — audit and trace decorations cannot be part of that registration without deciding the
/// composition at registration time, which is the defect this type exists to remove (issue #195).
/// Resolve this instead to see what the pipeline will actually call.
/// </remarks>
/// <param name="engine">The composed engine, or <see langword="null"/> for a retrieval-only pipeline.</param>
public sealed class ComposedAnswerEngine(IAnswerEngine? engine)
{
    /// <summary>The engine <c>RagPipeline</c> answers with, decorations applied.</summary>
    /// <value><see langword="null"/> when neither an engine nor a chat client is registered.</value>
    public IAnswerEngine? Engine { get; } = engine;
}

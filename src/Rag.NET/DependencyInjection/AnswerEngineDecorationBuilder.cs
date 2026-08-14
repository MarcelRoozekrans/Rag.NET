using Rag.NET.Abstractions;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Collects the decorators that wrap whichever <see cref="IAnswerEngine"/> the pipeline ends up
/// using, so a package that observes answers does not have to be registered after the package that
/// generates them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every answer engine registers itself as <c>IAnswerEngine</c>
/// (<c>UseMapReduceAnswerEngine</c>, <c>UseRefineAnswerEngine</c>, <c>UseFlare</c>,
/// <c>UsePromptHardening</c>, …) and last-wins is the right rule for <i>choosing</i> one. It is the
/// wrong rule for <i>observing</i> one: <c>UseAuditLog</c> and <c>AddRagDiagnostics</c> registered
/// <c>IAnswerEngine</c> too, so <c>rag.UseAuditLog().UseMapReduceAnswerEngine()</c> ended with an
/// unwrapped engine and no answers audited at all — issue #195. A decorator collected here is
/// applied when the engine is composed, so it wraps the engine that is actually used whichever
/// order the two calls were made in, including a chat client registered after <c>AddRagNet</c>
/// returned.
/// </para>
/// <para>
/// The instance is registered by <c>AddRagNet</c> and mutated in place, exactly as the two pipeline
/// builders are (see <see cref="PipelineBuilderAccessors"/>): composition happens on first
/// resolution, so a <c>Use*</c> method running later still changes what the container composes.
/// </para>
/// </remarks>
public sealed class AnswerEngineDecorationBuilder
{
    private readonly List<string> _keys = [];
    private readonly List<Func<IAnswerEngine, IServiceProvider, IAnswerEngine>> _decorations = [];

    /// <summary>Adds a decorator, unless one with the same <paramref name="key"/> was already added.</summary>
    /// <param name="key">
    /// Identifies the decoration, normally the name of the <c>Use*</c> method adding it. Repeated
    /// calls with the same key are ignored (first wins), so a layered composition root that reaches
    /// <c>UseAuditLog</c> twice still audits each answer once — the same first-wins convention the
    /// idempotent <c>Use*</c> extensions carry.
    /// </param>
    /// <param name="decorate">
    /// Wraps the engine composed so far. Runs once, when the pipeline is first resolved, and must
    /// not resolve <see cref="IAnswerEngine"/> from the provider — the engine it is handed is the
    /// one to wrap.
    /// </param>
    /// <returns>This builder, so calls chain.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public AnswerEngineDecorationBuilder Add(
        string key,
        Func<IAnswerEngine, IServiceProvider, IAnswerEngine> decorate)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(decorate);

        if (_keys.Contains(key, StringComparer.Ordinal))
            return this;

        _keys.Add(key);
        _decorations.Add(decorate);

        return this;
    }

    /// <summary>Wraps <paramref name="engine"/> in every decorator added, in the order they were added.</summary>
    /// <param name="engine">
    /// The engine to decorate, or <see langword="null"/> for a retrieval-only pipeline — which is
    /// left alone: inventing an engine so there is something to observe would turn a pipeline that
    /// worked without diagnostics into one that throws.
    /// </param>
    /// <param name="serviceProvider">The provider each decorator resolves its own dependencies from.</param>
    /// <returns>The outermost decorator, or <paramref name="engine"/> when nothing was added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serviceProvider"/> is <see langword="null"/>.</exception>
    public IAnswerEngine? Apply(IAnswerEngine? engine, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (engine is null)
            return null;

        for (var i = 0; i < _decorations.Count; i++)
            engine = _decorations[i](engine, serviceProvider);

        return engine;
    }
}

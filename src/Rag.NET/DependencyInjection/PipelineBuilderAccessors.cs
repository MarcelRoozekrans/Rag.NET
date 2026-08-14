using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Reaches the two pipeline builders <see cref="ServiceCollectionExtensions.AddRagNet"/> put in
/// the collection, so a <c>Use*</c> extension in any package can place the behaviours it
/// registers instead of only registering them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists as shared API.</b> A behaviour that is registered but absent from the chain
/// is never resolved by <c>Build</c>, so it never runs — and nothing says so. Issue #191 found
/// three <c>Use*</c> methods in that state at once (<c>UseRaptor</c>, <c>UseGraphRag</c>,
/// <c>UseMindMapExtraction</c>), while three others had each hand-rolled the same four-line
/// descriptor search to avoid it. The difference between the two groups was not care; it was that
/// the seam was undiscoverable. One named, documented accessor is what makes placing a behaviour
/// the obvious thing to do rather than the thing you have to know about.
/// </para>
/// <para>
/// Both builders are registered as instances, are mutated in place, and are read lazily: the
/// singleton factories <c>AddRagNet</c> registers capture the builder and call <c>Build</c> on
/// first resolution, long after registration finishes. So a <c>Use*</c> method running inside
/// <c>configure</c> — which runs <em>after</em> the <c>ingestion:</c> and <c>retrieval:</c>
/// delegates — can still change the chain the container will compose.
/// </para>
/// </remarks>
public static class PipelineBuilderAccessors
{
    /// <summary>Gets the ingestion pipeline the container will compose.</summary>
    /// <param name="services">The collection <c>AddRagNet</c> was called on.</param>
    /// <param name="calledBy">
    /// The <c>Use*</c> or <c>Add*</c> method asking, named in the failure message so the caller is
    /// told which of their own lines is in the wrong place.
    /// </param>
    /// <returns>The live builder, ready for <see cref="IngestionPipelineBuilder.Add{T}"/>.</returns>
    /// <exception cref="InvalidOperationException"><c>AddRagNet</c> has not been called.</exception>
    public static IngestionPipelineBuilder RagIngestionPipeline(
        this IServiceCollection services, string calledBy) =>
        Find<IngestionPipelineBuilder>(services)
        ?? throw MissingAddRagNet(calledBy, nameof(IngestionPipelineBuilder));

    /// <summary>Gets the retrieval pipeline the container will compose.</summary>
    /// <param name="services">The collection <c>AddRagNet</c> was called on.</param>
    /// <param name="calledBy">
    /// The <c>Use*</c> or <c>Add*</c> method asking, named in the failure message so the caller is
    /// told which of their own lines is in the wrong place.
    /// </param>
    /// <returns>The live builder, ready for <see cref="RetrievalPipelineBuilder.Add{T}"/>.</returns>
    /// <exception cref="InvalidOperationException"><c>AddRagNet</c> has not been called.</exception>
    public static RetrievalPipelineBuilder RagRetrievalPipeline(
        this IServiceCollection services, string calledBy) =>
        Find<RetrievalPipelineBuilder>(services)
        ?? throw MissingAddRagNet(calledBy, nameof(RetrievalPipelineBuilder));

    /// <summary>
    /// Finds the builder instance without resolving anything: these accessors run during
    /// registration, when no <see cref="IServiceProvider"/> exists yet.
    /// </summary>
    /// <typeparam name="T">The builder type to find.</typeparam>
    /// <param name="services">The collection to search.</param>
    /// <returns>The registered instance, or <see langword="null"/> when there is none.</returns>
    private static T? Find<T>(IServiceCollection services)
        where T : class =>
        services.FirstOrDefault(static d => d.ServiceType == typeof(T))?.ImplementationInstance as T;

    /// <summary>Explains what was not called, and where the call that needs it belongs.</summary>
    /// <param name="calledBy">The method that could not find a pipeline.</param>
    /// <param name="builderName">The builder type that was missing.</param>
    /// <returns>The exception to throw.</returns>
    private static InvalidOperationException MissingAddRagNet(string calledBy, string builderName) =>
        new($"{calledBy} requires AddRagNet to have been called first, so that the {builderName} " +
            "its behaviour is inserted into exists. Call it from inside AddRagNet's configure " +
            "delegate, or on a RagBuilder built afterwards.");
}

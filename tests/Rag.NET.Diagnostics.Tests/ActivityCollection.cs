using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// Serialises the test classes that subscribe an <c>ActivityListener</c> to the <c>Rag.NET</c> source.
/// </summary>
/// <remarks>
/// <para>
/// An <c>ActivityListener</c> is process-wide: it hears every span from the source it listens to,
/// including spans another test class is emitting on another thread at the same time. Two such classes
/// running in parallel therefore record into each other's collectors, and the symptom is a buffer that
/// holds four traces where the test added one — a failure that has nothing to do with the code under
/// test and does not reproduce alone.
/// </para>
/// <para>
/// The same reason <c>Rag.NET.Tests</c> puts its telemetry tests in a <c>"Telemetry"</c> collection.
/// Classes that only start a bare <c>new Activity(...)</c> do not need this: an activity with no
/// <c>ActivitySource</c> behind it never reaches a listener at all.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ActivityCollection
{
    /// <summary>The collection name, so the attribute and its uses cannot drift apart.</summary>
    public const string Name = "Activity";
}

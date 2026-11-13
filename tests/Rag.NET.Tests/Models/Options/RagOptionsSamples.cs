using System.Reflection;
using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Tests.Models.Options;

/// <summary>
/// A <see cref="RagOptions"/> with every public settable property set to a value that differs
/// from its default, found by reflection, so a copy test fails when a new property is added and
/// the copy leaves it behind.
/// </summary>
internal static class RagOptionsSamples
{
    public static IReadOnlyList<PropertyInfo> SettableProperties { get; } =
        typeof(RagOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.SetMethod is { IsPublic: true })
            .ToArray();

    public static PropertyInfo Property(string name) =>
        SettableProperties.Single(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Sets every property in <see cref="SettableProperties"/> to a sample value. Throws for a
    /// property type that has no sample yet; add one to <see cref="SampleFor"/>.
    /// </summary>
    public static RagOptions AllSet()
    {
        var options = new RagOptions();
        foreach (var property in SettableProperties)
            property.SetValue(options, SampleFor(property));
        return options;
    }

    private static object SampleFor(PropertyInfo property)
    {
        var type = property.PropertyType;
        if (type == typeof(int) || type == typeof(int?)) return 42;
        if (type == typeof(double)) return 0.42;
        if (type == typeof(float) || type == typeof(float?)) return 0.42f;
        if (type == typeof(bool)) return true;
        if (type == typeof(string)) return "sample";
        if (type == typeof(SynthesisStrategy)) return SynthesisStrategy.Refine;
        if (type == typeof(IDictionary<string, MetadataValue>))
            return new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["k"] = "v" };
        if (type == typeof(IList<ChatMessage>))
            return new List<ChatMessage> { new(ChatRole.User, "earlier") };
        if (type == typeof(RetrievalOptions)) return new RetrievalOptions();
        if (type == typeof(MapReduceOptions)) return new MapReduceOptions();
        if (type == typeof(RefineOptions)) return new RefineOptions();

        throw new InvalidOperationException(
            $"RagOptions.{property.Name} has type {type}, which has no sample value. "
            + "Add one so the copy tests cover the new property.");
    }
}

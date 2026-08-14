namespace Rag.NET.DependencyInjection;

/// <summary>Set by a decorator factory to record that the decorator was actually constructed.</summary>
internal sealed class DecorationProbe
{
    /// <summary>Whether the decoration this probe belongs to reached the resolved graph.</summary>
    internal bool Applied { get; set; }
}

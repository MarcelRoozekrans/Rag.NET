namespace Rag.NET.Api.Contracts;

/// <summary>One conversation turn on the wire: a role and its text.</summary>
/// <param name="Role">The role, such as <c>user</c>, <c>assistant</c> or <c>system</c>.</param>
/// <param name="Text">The turn's text.</param>
public sealed record ChatMessageDto(string Role, string Text);

using Microsoft.Extensions.Logging;

namespace Rag.NET.Tests;

/// <summary>
/// Source-generated log call used only in scope tests to prove a <c>BeginScope</c> around a
/// downstream call is actually active when that downstream code logs — <c>FakeLogger</c> only
/// records the ambient scope stack at the moment <c>Log</c> is invoked, so a scope test needs a
/// real, structured log call inside the scoped region rather than a plain interpolated one.
/// </summary>
internal static partial class ScopeProbeLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "scope probe")]
    public static partial void Emit(ILogger logger);
}

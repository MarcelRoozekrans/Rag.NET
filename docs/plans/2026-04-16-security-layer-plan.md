# Security Layer Implementation Plan — RBAC, PII Detection, Audit Log

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add three independent, composable security features to `Rag.NET.Security`: role-based access control on retrieved chunks, PII detection and redaction at ingest time, and a structured audit log of every retrieval and answer.

**Architecture:** Each feature follows the existing pattern of `Rag.NET.Security` — new implementations of existing abstractions (`IRetrievalGuard`, `IChunkSanitiser`) plus new abstractions (`ICallerContext`, `IAuditLog`) with `RagBuilder` extension methods. RBAC uses a context-aware `ICallerContext` resolved by `RbacRetrievalGuard`. PII uses two `IChunkSanitiser` implementations chained by the existing `ChunkSanitiserBehavior`. Audit log uses a new `AuditRetrievalBehavior` (pipeline behavior) and `AuditAnswerEngineDecorator`; to wire the behavior from outside the core package, `ServiceCollectionExtensions.AddRagNet` is extended to register `RetrievalPipelineBuilder` in DI.

**Tech Stack:** C# 13 / .NET 10, `Microsoft.Data.Sqlite` (already in codebase), `Microsoft.AspNetCore.Http.Abstractions` (for AspNetCore package), `[GeneratedRegex]`, `[LoggerMessage]`, `[Singleton]` from ZeroAlloc.Inject, NSubstitute + xUnit v3 for tests.

---

## Key conventions

- All tests use `TestContext.Current.CancellationToken` (not `CancellationToken.None`) — xUnit1051 rule
- String comparisons always supply `StringComparison.Ordinal` or `OrdinalIgnoreCase` — MA0006 rule
- No `// TODO` comments — MA0026 rule
- `[LoggerMessage]` partial static methods for all structured log entries
- NSubstitute: `using NSubstitute.ExceptionExtensions` for `.ThrowsAsync()`
- `TreatWarningsAsErrors = true` — all code must be clean
- `[GeneratedRegex]` for compile-time patterns; `new Regex(pattern, RegexOptions.Compiled)` for runtime patterns
- Test helper pattern: `private static SearchResult MakeResult(...)` in test class (see `TrustLevelRetrievalGuardTests.cs`)
- `ValueTask`-returning behavior methods use `.AsTask()` for `Assert.ThrowsAsync`

---

## Task 1: `ICallerContext` abstraction + `PiiDetectionOptions` types

**Files:**
- Create: `src/Rag.NET.Security/ICallerContext.cs`
- Create: `src/Rag.NET.Security/Pii/PiiPattern.cs`
- Create: `src/Rag.NET.Security/Pii/PiiPatterns.cs`
- Create: `src/Rag.NET.Security/Pii/PiiDetectionOptions.cs`

**Step 1: Create `ICallerContext`**

```csharp
// src/Rag.NET.Security/ICallerContext.cs
namespace Rag.NET.Security;

/// <summary>
/// Provides the roles of the current caller for RBAC chunk filtering.
/// Implement as a singleton using <c>IHttpContextAccessor</c> (ASP.NET Core)
/// or <c>AsyncLocal&lt;IReadOnlyList&lt;string&gt;&gt;</c> (other hosts).
/// Return an empty list when no caller context is available — RBAC will pass all chunks through.
/// </summary>
public interface ICallerContext
{
    /// <summary>Returns the roles of the current caller.</summary>
    IReadOnlyList<string> GetRoles();
}
```

**Step 2: Create PII types**

```csharp
// src/Rag.NET.Security/Pii/PiiPattern.cs
namespace Rag.NET.Security;

/// <summary>A regex pattern and its replacement placeholder for PII detection.</summary>
public sealed record PiiPattern
{
    /// <summary>The placeholder inserted in place of matched text, e.g. <c>[EMAIL]</c>.</summary>
    public required string Placeholder { get; init; }

    /// <summary>The regular expression pattern. Compiled at sanitiser construction time.</summary>
    public required string RegexPattern { get; init; }
}
```

```csharp
// src/Rag.NET.Security/Pii/PiiPatterns.cs
namespace Rag.NET.Security;

/// <summary>Built-in PII patterns. Reference these to remove specific defaults from <see cref="PiiDetectionOptions"/>.</summary>
public static class PiiPatterns
{
    public static readonly PiiPattern Email = new()
    {
        Placeholder = "[EMAIL]",
        RegexPattern = @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b"
    };

    public static readonly PiiPattern Phone = new()
    {
        Placeholder = "[PHONE]",
        RegexPattern = @"(?:\+?1[\s\-.]?)?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}\b"
    };

    public static readonly PiiPattern Ssn = new()
    {
        Placeholder = "[SSN]",
        RegexPattern = @"\b\d{3}-\d{2}-\d{4}\b"
    };

    public static readonly PiiPattern CreditCard = new()
    {
        Placeholder = "[CREDIT_CARD]",
        RegexPattern = @"\b(?:4\d{3}|5[1-5]\d{2}|6011|3[47]\d{2})[\s\-]?\d{4}[\s\-]?\d{4}[\s\-]?\d{4}\b"
    };

    public static readonly PiiPattern IpAddress = new()
    {
        Placeholder = "[IP_ADDRESS]",
        RegexPattern = @"\b(?:\d{1,3}\.){3}\d{1,3}\b|(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b"
    };

    /// <summary>All five built-in patterns in their default order.</summary>
    public static IReadOnlyList<PiiPattern> Defaults { get; } =
        [Email, Phone, Ssn, CreditCard, IpAddress];
}
```

```csharp
// src/Rag.NET.Security/Pii/PiiDetectionOptions.cs
namespace Rag.NET.Security;

/// <summary>
/// Configures which PII patterns <see cref="PiiChunkSanitiser"/> detects and redacts.
/// Pre-populated with <see cref="PiiPatterns.Defaults"/>. Add or remove entries to customise.
/// </summary>
public sealed class PiiDetectionOptions
{
    /// <summary>
    /// The active PII patterns. Patterns are compiled at <see cref="PiiChunkSanitiser"/> construction time.
    /// </summary>
    public IList<PiiPattern> Patterns { get; init; } = PiiPatterns.Defaults.ToList();
}
```

**Step 3: Commit**

```bash
git add src/Rag.NET.Security/ICallerContext.cs \
        src/Rag.NET.Security/Pii/PiiPattern.cs \
        src/Rag.NET.Security/Pii/PiiPatterns.cs \
        src/Rag.NET.Security/Pii/PiiDetectionOptions.cs
git commit -m "feat(security): add ICallerContext, PiiPattern, PiiPatterns, PiiDetectionOptions"
```

---

## Task 2: `RbacRetrievalGuard` with tests

**Files:**
- Create: `src/Rag.NET.Security/RbacRetrievalGuard.cs`
- Create: `tests/Rag.NET.Security.Tests/RbacRetrievalGuardTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/Rag.NET.Security.Tests/RbacRetrievalGuardTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class RbacRetrievalGuardTests
{
    private static SearchResult MakeResult(string? allowedRoles, string docId = "doc1")
    {
        var chunk = new TextChunk
        {
            Text = "chunk text",
            DocumentId = new DocumentId(docId),
            ChunkIndex = 0,
        };
        if (allowedRoles is not null)
            chunk.Metadata["allowed_roles"] = allowedRoles;
        return new SearchResult { Score = 1.0, Chunk = chunk };
    }

    private static ICallerContext CallerWith(params string[] roles)
    {
        var ctx = Substitute.For<ICallerContext>();
        ctx.GetRoles().Returns(roles);
        return ctx;
    }

    [Fact]
    public void Inspect_NoAllowedRoles_PassesThrough()
    {
        var sut = new RbacRetrievalGuard(CallerWith("user"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult(null) };
        Assert.Single(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_CallerHasMatchingRole_PassesThrough()
    {
        var sut = new RbacRetrievalGuard(CallerWith("hr"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr,finance") };
        Assert.Single(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_CallerLacksRole_ChunkFiltered()
    {
        var sut = new RbacRetrievalGuard(CallerWith("user"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr,finance") };
        Assert.Empty(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_EmptyCallerRoles_RestrictedChunkFiltered()
    {
        var sut = new RbacRetrievalGuard(CallerWith(), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr") };
        Assert.Empty(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_MixedChunks_OnlyRestrictedFiltered()
    {
        var sut = new RbacRetrievalGuard(CallerWith("hr"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[]
        {
            MakeResult(null, "public"),
            MakeResult("hr", "hr-only"),
            MakeResult("finance", "finance-only"),
        };
        var inspected = sut.Inspect(results);
        Assert.Equal(2, inspected.Count);
        Assert.DoesNotContain(inspected, r => string.Equals(r.Chunk.DocumentId.Value, "finance-only", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_RoleCaseInsensitive_Matches()
    {
        var sut = new RbacRetrievalGuard(CallerWith("HR"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new[] { MakeResult("hr") };
        Assert.Single(sut.Inspect(results));
    }

    [Fact]
    public void Inspect_AllPublicChunks_ReturnsOriginalList()
    {
        var sut = new RbacRetrievalGuard(CallerWith("user"), NullLogger<RbacRetrievalGuard>.Instance);
        var results = new List<SearchResult> { MakeResult(null), MakeResult(null) }.AsReadOnly();
        Assert.Same(results, sut.Inspect(results));
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter RbacRetrievalGuard
```
Expected: compile errors — `RbacRetrievalGuard` not defined.

**Step 3: Implement `RbacRetrievalGuard`**

```csharp
// src/Rag.NET.Security/RbacRetrievalGuard.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Security;

/// <summary>
/// Filters retrieved chunks to those whose <c>allowed_roles</c> metadata intersects the caller's roles.
/// Chunks with no <c>allowed_roles</c> metadata are world-readable and always pass through.
/// Pass-through when no roles are restricted or all chunks are public.
/// </summary>
public sealed partial class RbacRetrievalGuard(
    ICallerContext callerContext,
    ILogger<RbacRetrievalGuard>? logger = null) : IRetrievalGuard
{
    private readonly ILogger<RbacRetrievalGuard> _logger =
        logger ?? NullLogger<RbacRetrievalGuard>.Instance;

    public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results)
    {
        var callerRoles = callerContext.GetRoles();

        List<SearchResult>? filtered = null;
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (!result.Chunk.Metadata.TryGetValue("allowed_roles", out var allowedRolesRaw))
            {
                filtered?.Add(result);
                continue;
            }

            var allowedRoles = allowedRolesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var hasAccess = callerRoles.Any(r =>
                allowedRoles.Any(a => string.Equals(a, r, StringComparison.OrdinalIgnoreCase)));

            if (!hasAccess)
            {
                LogAccessDenied(_logger, result.Chunk.DocumentId.Value);
                if (filtered is null)
                {
                    filtered = new List<SearchResult>(i);
                    for (var j = 0; j < i; j++)
                        filtered.Add(results[j]);
                }
                continue;
            }

            filtered?.Add(result);
        }
        return filtered is not null ? filtered.AsReadOnly() : results;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "RBAC: chunk from '{DocumentId}' filtered — caller lacks required role.")]
    private static partial void LogAccessDenied(ILogger logger, string documentId);
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter RbacRetrievalGuard
```
Expected: all tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET.Security/RbacRetrievalGuard.cs \
        tests/Rag.NET.Security.Tests/RbacRetrievalGuardTests.cs
git commit -m "feat(security): add RbacRetrievalGuard with role-based chunk filtering"
```

---

## Task 3: `PiiChunkSanitiser` with tests

**Files:**
- Create: `src/Rag.NET.Security/Pii/PiiChunkSanitiser.cs`
- Create: `tests/Rag.NET.Security.Tests/PiiChunkSanitiserTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/Rag.NET.Security.Tests/PiiChunkSanitiserTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class PiiChunkSanitiserTests
{
    private static PiiChunkSanitiser Sut(Action<PiiDetectionOptions>? configure = null)
    {
        var opts = new PiiDetectionOptions();
        configure?.Invoke(opts);
        return new PiiChunkSanitiser(opts, NullLogger<PiiChunkSanitiser>.Instance);
    }

    private static readonly IReadOnlyDictionary<string, string> Meta =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["file_name"] = "test.txt" };

    [Fact]
    public void Sanitise_Email_Redacted()
    {
        var result = Sut().Sanitise("Contact us at alice@example.com for help.", Meta);
        Assert.Contains("[EMAIL]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_Phone_Redacted()
    {
        var result = Sut().Sanitise("Call us at 555-867-5309.", Meta);
        Assert.Contains("[PHONE]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("555-867-5309", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_Ssn_Redacted()
    {
        var result = Sut().Sanitise("SSN: 123-45-6789", Meta);
        Assert.Contains("[SSN]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("123-45-6789", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_IpAddress_Redacted()
    {
        var result = Sut().Sanitise("Server IP is 192.168.1.1", Meta);
        Assert.Contains("[IP_ADDRESS]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_NoPii_ReturnsOriginal()
    {
        const string text = "The quick brown fox jumps over the lazy dog.";
        Assert.Equal(text, Sut().Sanitise(text, Meta));
    }

    [Fact]
    public void Sanitise_MultiplePiiInSameText_AllRedacted()
    {
        var result = Sut().Sanitise("Email alice@example.com, IP 10.0.0.1", Meta);
        Assert.Contains("[EMAIL]", result, StringComparison.Ordinal);
        Assert.Contains("[IP_ADDRESS]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_CustomPattern_Redacted()
    {
        var result = Sut(o => o.Patterns.Add(new PiiPattern
        {
            Placeholder = "[EMPLOYEE_ID]",
            RegexPattern = @"\bEMP-\d{6}\b"
        })).Sanitise("Employee EMP-001234 is on leave.", Meta);
        Assert.Contains("[EMPLOYEE_ID]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_RemovedBuiltIn_NotRedacted()
    {
        var email = "alice@example.com";
        var result = Sut(o => o.Patterns.Remove(PiiPatterns.Email))
            .Sanitise($"Email: {email}", Meta);
        Assert.Contains(email, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_NullText_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Sut().Sanitise(null!, Meta));
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter PiiChunkSanitiser
```

**Step 3: Implement `PiiChunkSanitiser`**

```csharp
// src/Rag.NET.Security/Pii/PiiChunkSanitiser.cs
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;

namespace Rag.NET.Security;

/// <summary>
/// Detects and redacts PII from chunk text at ingest time using compiled regex patterns.
/// Patterns are configurable via <see cref="PiiDetectionOptions"/>. Each match is replaced
/// with a typed placeholder (e.g. <c>[EMAIL]</c>) and logged at Warning level.
/// Never throws — returns original text on failure.
/// </summary>
public sealed partial class PiiChunkSanitiser : IChunkSanitiser
{
    private readonly ILogger<PiiChunkSanitiser> _logger;
    private readonly IReadOnlyList<(Regex Regex, string Placeholder)> _compiled;

    public PiiChunkSanitiser(PiiDetectionOptions options, ILogger<PiiChunkSanitiser>? logger = null)
    {
        _logger = logger ?? NullLogger<PiiChunkSanitiser>.Instance;
        _compiled = options.Patterns
            .Select(p => (new Regex(p.RegexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), p.Placeholder))
            .ToList()
            .AsReadOnly();
    }

    public string Sanitise(string text, IReadOnlyDictionary<string, string> metadata)
    {
        if (text is null) return string.Empty;
        try
        {
            var fileName = metadata.TryGetValue("file_name", out var fn) ? fn : "<unknown>";
            var result = text;
            foreach (var (regex, placeholder) in _compiled)
            {
                result = regex.Replace(result, m =>
                {
                    LogPiiDetected(_logger, placeholder, fileName);
                    return placeholder;
                });
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSanitiseFailed(_logger, ex);
            return text;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "PII detected in chunk from '{FileName}': replaced with {Placeholder}.")]
    private static partial void LogPiiDetected(ILogger logger, string placeholder, string fileName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "PiiChunkSanitiser failed; returning original text.")]
    private static partial void LogSanitiseFailed(ILogger logger, Exception ex);
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter PiiChunkSanitiser
```

**Step 5: Commit**

```bash
git add src/Rag.NET.Security/Pii/PiiChunkSanitiser.cs \
        tests/Rag.NET.Security.Tests/PiiChunkSanitiserTests.cs
git commit -m "feat(security): add PiiChunkSanitiser with configurable regex patterns"
```

---

## Task 4: `LlmPiiChunkSanitiser` with tests

**Files:**
- Create: `src/Rag.NET.Security/Pii/LlmPiiChunkSanitiser.cs`
- Create: `tests/Rag.NET.Security.Tests/LlmPiiChunkSanitiserTests.cs`

**Step 1: Write failing tests**

```csharp
// tests/Rag.NET.Security.Tests/LlmPiiChunkSanitiserTests.cs
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class LlmPiiChunkSanitiserTests
{
    private static readonly IReadOnlyDictionary<string, string> Meta =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["file_name"] = "test.txt" };

    private static IChatClient ClientReturning(string text)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))));
        return client;
    }

    [Fact]
    public void Sanitise_LlmReturnsRedactedText_UsesLlmOutput()
    {
        const string redacted = "Contact [EMAIL] for help.";
        var sut = new LlmPiiChunkSanitiser(ClientReturning(redacted), NullLogger<LlmPiiChunkSanitiser>.Instance);
        var result = sut.Sanitise("Contact alice@example.com for help.", Meta);
        Assert.Equal(redacted, result);
    }

    [Fact]
    public void Sanitise_LlmThrows_FallsBackToRegex()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new HttpRequestException("network error"));
        var sut = new LlmPiiChunkSanitiser(client, NullLogger<LlmPiiChunkSanitiser>.Instance);
        // Regex fallback should redact the email
        var result = sut.Sanitise("Email alice@example.com here.", Meta);
        Assert.Contains("[EMAIL]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitise_NullText_ReturnsEmpty()
    {
        var sut = new LlmPiiChunkSanitiser(ClientReturning(""), NullLogger<LlmPiiChunkSanitiser>.Instance);
        Assert.Equal(string.Empty, sut.Sanitise(null!, Meta));
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter LlmPiiChunkSanitiser
```

**Step 3: Implement `LlmPiiChunkSanitiser`**

Model after `LlmChunkSanitiser.cs` (same file, same sync-over-async pattern, same fallback approach):

```csharp
// src/Rag.NET.Security/Pii/LlmPiiChunkSanitiser.cs
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;

namespace Rag.NET.Security;

/// <summary>
/// Detects and redacts PII from chunk text using an LLM call.
/// Runs after <see cref="PiiChunkSanitiser"/> when both are registered.
/// Falls back to <see cref="PiiChunkSanitiser"/> on LLM failure.
/// Never throws — returns original (or regex-sanitised) text on failure.
/// </summary>
public sealed partial class LlmPiiChunkSanitiser(
    IChatClient chatClient,
    ILogger<LlmPiiChunkSanitiser>? logger = null) : IChunkSanitiser
{
    private readonly ILogger<LlmPiiChunkSanitiser> _logger =
        logger ?? NullLogger<LlmPiiChunkSanitiser>.Instance;
    private readonly PiiChunkSanitiser _fallback =
        new(new PiiDetectionOptions(), NullLogger<PiiChunkSanitiser>.Instance);

    private const string PiiPrompt =
        "Return the following text with all personally identifiable information (PII) replaced " +
        "by typed placeholders such as [EMAIL], [PHONE], [SSN], [CREDIT_CARD], [IP_ADDRESS], [NAME]. " +
        "Return only the modified text with no explanation.\n\nText:\n{text}";

    public string Sanitise(string text, IReadOnlyDictionary<string, string> metadata)
    {
        if (text is null) return string.Empty;
        var fileName = metadata.TryGetValue("file_name", out var fn) ? fn : "<unknown>";
        try
        {
            var prompt = PiiPrompt.Replace("{text}", text, StringComparison.Ordinal);
            var response = chatClient
                .GetResponseAsync([new ChatMessage(ChatRole.User, prompt)])
                .ConfigureAwait(false).GetAwaiter().GetResult();
            var result = response.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(result))
            {
                LogLlmPiiRedacted(_logger, fileName);
                return result;
            }
            return text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogLlmFailed(_logger, ex);
            return _fallback.Sanitise(text, metadata);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "LLM PII sanitiser redacted content in chunk from '{FileName}'.")]
    private static partial void LogLlmPiiRedacted(ILogger logger, string fileName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "LLM PII sanitiser failed; falling back to regex sanitiser.")]
    private static partial void LogLlmFailed(ILogger logger, Exception ex);
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter LlmPiiChunkSanitiser
```

**Step 5: Commit**

```bash
git add src/Rag.NET.Security/Pii/LlmPiiChunkSanitiser.cs \
        tests/Rag.NET.Security.Tests/LlmPiiChunkSanitiserTests.cs
git commit -m "feat(security): add LlmPiiChunkSanitiser with regex fallback"
```

---

## Task 5: `IAuditLog` abstraction + event records + `NoOpAuditLog`

**Files:**
- Create: `src/Rag.NET.Security/Audit/AuditChunkRef.cs`
- Create: `src/Rag.NET.Security/Audit/AuditRetrievalEvent.cs`
- Create: `src/Rag.NET.Security/Audit/AuditAnswerEvent.cs`
- Create: `src/Rag.NET.Security/Audit/IAuditLog.cs`
- Create: `src/Rag.NET.Security/Audit/NoOpAuditLog.cs`
- Create: `src/Rag.NET.Security/Audit/AuditLogOptions.cs`

**Step 1: Create event records and abstraction**

```csharp
// src/Rag.NET.Security/Audit/AuditChunkRef.cs
namespace Rag.NET.Security;

/// <summary>A reference to a chunk that appeared in a retrieval result.</summary>
public sealed record AuditChunkRef
{
    public required string DocumentId { get; init; }
    public required int    ChunkIndex { get; init; }
    public required double Score      { get; init; }
}
```

```csharp
// src/Rag.NET.Security/Audit/AuditRetrievalEvent.cs
namespace Rag.NET.Security;

/// <summary>Records a retrieval operation for audit purposes.</summary>
public sealed record AuditRetrievalEvent
{
    /// <summary>Correlates this retrieval event with the corresponding <see cref="AuditAnswerEvent"/>.</summary>
    public required string RequestId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required IReadOnlyList<string> CallerRoles { get; init; }
    public required IReadOnlyList<AuditChunkRef> Chunks { get; init; }
    /// <summary>The raw query string. Only populated when <see cref="AuditLogOptions.LogQueryText"/> is <see langword="true"/>.</summary>
    public string? Query { get; init; }
}
```

```csharp
// src/Rag.NET.Security/Audit/AuditAnswerEvent.cs
namespace Rag.NET.Security;

/// <summary>Records an answer generation operation for audit purposes.</summary>
public sealed record AuditAnswerEvent
{
    public required string RequestId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>The generated answer text. Only populated when <see cref="AuditLogOptions.LogAnswerText"/> is <see langword="true"/>.</summary>
    public string? Answer { get; init; }
}
```

```csharp
// src/Rag.NET.Security/Audit/IAuditLog.cs
namespace Rag.NET.Security;

/// <summary>
/// Structured audit trail of retrieval and answer-generation operations.
/// Implementations must never throw — errors should be logged internally.
/// </summary>
public interface IAuditLog
{
    ValueTask LogRetrievalAsync(AuditRetrievalEvent ev, CancellationToken ct = default);
    ValueTask LogAnswerAsync(AuditAnswerEvent ev, CancellationToken ct = default);
}
```

```csharp
// src/Rag.NET.Security/Audit/NoOpAuditLog.cs
namespace Rag.NET.Security;

/// <summary>No-op <see cref="IAuditLog"/> used when audit logging is not configured.</summary>
public sealed class NoOpAuditLog : IAuditLog
{
    public static readonly NoOpAuditLog Instance = new();
    public ValueTask LogRetrievalAsync(AuditRetrievalEvent ev, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask LogAnswerAsync(AuditAnswerEvent ev, CancellationToken ct = default) => ValueTask.CompletedTask;
}
```

```csharp
// src/Rag.NET.Security/Audit/AuditLogOptions.cs
namespace Rag.NET.Security;

/// <summary>Controls what the audit log captures.</summary>
public sealed class AuditLogOptions
{
    /// <summary>When <see langword="true"/>, the raw query string is stored in <see cref="AuditRetrievalEvent.Query"/>.</summary>
    public bool LogQueryText { get; set; } = false;

    /// <summary>When <see langword="true"/>, the generated answer text is stored in <see cref="AuditAnswerEvent.Answer"/>.</summary>
    public bool LogAnswerText { get; set; } = false;

    /// <summary>Path to the SQLite database file used by <see cref="SqliteAuditLog"/>.</summary>
    public string DatabasePath { get; set; } = "rag-audit.db";
}
```

**Step 2: Commit**

```bash
git add src/Rag.NET.Security/Audit/
git commit -m "feat(security): add IAuditLog, audit event records, NoOpAuditLog, AuditLogOptions"
```

---

## Task 6: Expose `RetrievalPipelineBuilder` in DI + `AuditRetrievalBehavior`

`AuditRetrievalBehavior` needs access to `RetrievalContext.Query`, so it must be an `IRetrievalBehavior` in the pipeline. The `RetrievalPipelineBuilder` is currently local to `AddRagNet` and not registered in DI. Add a one-line registration so extension packages can add behaviors to it.

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs` (line ~38 — after `var retrievalBuilder = new RetrievalPipelineBuilder();`)
- Create: `src/Rag.NET.Security/Audit/AuditRetrievalBehavior.cs`
- Create: `tests/Rag.NET.Security.Tests/AuditRetrievalBehaviorTests.cs`

**Step 1: Register `RetrievalPipelineBuilder` in DI**

In `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`, after line `var retrievalBuilder = new RetrievalPipelineBuilder();`:

```csharp
var retrievalBuilder = new RetrievalPipelineBuilder();
retrieval?.Invoke(retrievalBuilder);
services.AddSingleton(retrievalBuilder);   // ← ADD THIS LINE
services.AddSingleton(sp => retrievalBuilder.Build(sp));
```

**Step 2: Write failing tests**

```csharp
// tests/Rag.NET.Security.Tests/AuditRetrievalBehaviorTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class AuditRetrievalBehaviorTests
{
    private static RetrievalContext MakeCtx(string query = "test query", ILogger? logger = null) =>
        new(query, new RetrievalOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    private static SearchResult MakeResult(string docId) =>
        new() { Score = 0.9, Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId(docId), ChunkIndex = 0 } };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    [Fact]
    public async Task HandleAsync_LogsRetrievalEvent_WithChunkRefs()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns(["user"]);
        var opts = new AuditLogOptions();
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, opts, NullLogger<AuditRetrievalBehavior>.Instance);

        var results = new[] { MakeResult("doc-1"), MakeResult("doc-2") };
        var ctx = MakeCtx("my query");

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning(results));

        await auditLog.Received(1).LogRetrievalAsync(
            Arg.Is<AuditRetrievalEvent>(e =>
                e.Chunks.Count == 2 &&
                string.Equals(e.CallerRoles[0], "user", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LogQueryTextFalse_QueryIsNull()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns([]);
        var opts = new AuditLogOptions { LogQueryText = false };
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, opts, NullLogger<AuditRetrievalBehavior>.Instance);

        await sut.HandleAsync(MakeCtx("secret"), TestContext.Current.CancellationToken, NextReturning([]));

        await auditLog.Received(1).LogRetrievalAsync(
            Arg.Is<AuditRetrievalEvent>(e => e.Query == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LogQueryTextTrue_QueryPopulated()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns([]);
        var opts = new AuditLogOptions { LogQueryText = true };
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, opts, NullLogger<AuditRetrievalBehavior>.Instance);

        await sut.HandleAsync(MakeCtx("find me"), TestContext.Current.CancellationToken, NextReturning([]));

        await auditLog.Received(1).LogRetrievalAsync(
            Arg.Is<AuditRetrievalEvent>(e => string.Equals(e.Query, "find me", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_SetsRequestIdInExtensions()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns([]);
        var opts = new AuditLogOptions();
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, opts, NullLogger<AuditRetrievalBehavior>.Instance);
        var ctx = MakeCtx();

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, NextReturning([]));

        Assert.True(ctx.Extensions.ContainsKey("audit_request_id"));
    }

    [Fact]
    public async Task HandleAsync_AuditLogThrows_ResultsStillReturned()
    {
        var auditLog = Substitute.For<IAuditLog>();
        auditLog.LogRetrievalAsync(Arg.Any<AuditRetrievalEvent>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("db error"));
        var callerCtx = Substitute.For<ICallerContext>();
        callerCtx.GetRoles().Returns([]);
        var sut = new AuditRetrievalBehavior(auditLog, callerCtx, new AuditLogOptions(), NullLogger<AuditRetrievalBehavior>.Instance);
        var results = new[] { MakeResult("doc-1") };

        var returned = await sut.HandleAsync(MakeCtx(), TestContext.Current.CancellationToken, NextReturning(results));

        Assert.Single(returned);
    }
}
```

**Step 3: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter AuditRetrievalBehavior
```

**Step 4: Implement `AuditRetrievalBehavior`**

```csharp
// src/Rag.NET.Security/Audit/AuditRetrievalBehavior.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Retrieval;

namespace Rag.NET.Security;

/// <summary>
/// Retrieval pipeline behavior that writes a structured <see cref="AuditRetrievalEvent"/> after
/// each retrieval. Stores the <c>RequestId</c> in <c>ctx.Extensions["audit_request_id"]</c>
/// so <see cref="AuditAnswerEngineDecorator"/> can correlate retrieval and answer events.
/// Errors from <see cref="IAuditLog"/> are swallowed and logged — results are always returned.
/// </summary>
public sealed partial class AuditRetrievalBehavior(
    IAuditLog auditLog,
    ICallerContext callerContext,
    AuditLogOptions options,
    ILogger<AuditRetrievalBehavior>? logger = null) : IRetrievalBehavior
{
    private readonly ILogger<AuditRetrievalBehavior> _logger =
        logger ?? NullLogger<AuditRetrievalBehavior>.Instance;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        var requestId = Guid.NewGuid().ToString("N");
        ctx.Extensions["audit_request_id"] = requestId;

        var ev = new AuditRetrievalEvent
        {
            RequestId    = requestId,
            Timestamp    = DateTimeOffset.UtcNow,
            CallerRoles  = callerContext.GetRoles(),
            Chunks       = results.Select(r => new AuditChunkRef
            {
                DocumentId = r.Chunk.DocumentId.Value,
                ChunkIndex = r.Chunk.ChunkIndex,
                Score      = r.Score,
            }).ToList().AsReadOnly(),
            Query = options.LogQueryText ? ctx.Query : null,
        };

        try
        {
            await auditLog.LogRetrievalAsync(ev, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogAuditFailed(_logger, ex);
        }

        return results;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AuditRetrievalBehavior failed to write audit log entry.")]
    private static partial void LogAuditFailed(ILogger logger, Exception ex);
}
```

**Step 5: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter AuditRetrievalBehavior
```

**Step 6: Commit**

```bash
git add src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs \
        src/Rag.NET.Security/Audit/AuditRetrievalBehavior.cs \
        tests/Rag.NET.Security.Tests/AuditRetrievalBehaviorTests.cs
git commit -m "feat(security): expose RetrievalPipelineBuilder in DI; add AuditRetrievalBehavior"
```

---

## Task 7: `AuditAnswerEngineDecorator` + `SqliteAuditLog`

**Files:**
- Create: `src/Rag.NET.Security/Audit/AuditAnswerEngineDecorator.cs`
- Create: `src/Rag.NET.Security/Audit/SqliteAuditLog.cs`
- Create: `tests/Rag.NET.Security.Tests/AuditAnswerEngineDecoratorTests.cs`

**Step 1: Write failing tests for `AuditAnswerEngineDecorator`**

```csharp
// tests/Rag.NET.Security.Tests/AuditAnswerEngineDecoratorTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class AuditAnswerEngineDecoratorTests
{
    private static IAnswerEngine EngineReturning(string answer)
    {
        var engine = Substitute.For<IAnswerEngine>();
        engine.AskAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<string?>(answer));
        return engine;
    }

    private static SearchResult MakeResult() =>
        new() { Score = 1.0, Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d"), ChunkIndex = 0 } };

    [Fact]
    public async Task AskAsync_LogsAnswerEvent_WithRequestId()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var opts = new AuditLogOptions { LogAnswerText = true };
        var ctx = new Dictionary<string, object?>(StringComparer.Ordinal) { ["audit_request_id"] = "req-123" };
        var sut = new AuditAnswerEngineDecorator(EngineReturning("The answer."), auditLog, opts, NullLogger<AuditAnswerEngineDecorator>.Instance);

        await sut.AskAsync("q", [MakeResult()], null, TestContext.Current.CancellationToken, ctx);

        await auditLog.Received(1).LogAnswerAsync(
            Arg.Is<AuditAnswerEvent>(e =>
                string.Equals(e.RequestId, "req-123", StringComparison.Ordinal) &&
                string.Equals(e.Answer, "The answer.", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_LogAnswerTextFalse_AnswerIsNull()
    {
        var auditLog = Substitute.For<IAuditLog>();
        var opts = new AuditLogOptions { LogAnswerText = false };
        var ctx = new Dictionary<string, object?>(StringComparer.Ordinal) { ["audit_request_id"] = "req-456" };
        var sut = new AuditAnswerEngineDecorator(EngineReturning("secret"), auditLog, opts, NullLogger<AuditAnswerEngineDecorator>.Instance);

        await sut.AskAsync("q", [MakeResult()], null, TestContext.Current.CancellationToken, ctx);

        await auditLog.Received(1).LogAnswerAsync(
            Arg.Is<AuditAnswerEvent>(e => e.Answer == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_AuditLogThrows_AnswerStillReturned()
    {
        var auditLog = Substitute.For<IAuditLog>();
        auditLog.LogAnswerAsync(Arg.Any<AuditAnswerEvent>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("db error"));
        var sut = new AuditAnswerEngineDecorator(EngineReturning("the answer"), auditLog, new AuditLogOptions(), NullLogger<AuditAnswerEngineDecorator>.Instance);

        var result = await sut.AskAsync("q", [MakeResult()], null, TestContext.Current.CancellationToken);

        Assert.Equal("the answer", result);
    }
}
```

> **Note:** The `AskAsync` signature in `IAnswerEngine` may differ — check `src/Rag.NET.Abstractions/Abstractions/IAnswerEngine.cs` before implementing and adjust the test and implementation to match the actual interface.

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter AuditAnswerEngineDecorator
```

**Step 3: Implement `AuditAnswerEngineDecorator`**

Model after `PromptHardeningAnswerEngineDecorator.cs`. The decorator wraps any `IAnswerEngine`, reads `audit_request_id` from the retrieval extensions (passed in a context parameter), and fire-and-forgets the audit log write.

Check `IAnswerEngine.cs` and `PromptHardeningAnswerEngineDecorator.cs` for the exact method signatures and implement accordingly.

**Step 4: Implement `SqliteAuditLog`**

```csharp
// src/Rag.NET.Security/Audit/SqliteAuditLog.cs
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rag.NET.Security;

/// <summary>
/// Persists audit events to a SQLite database. Writes are fire-and-forget.
/// Errors are logged, never thrown to callers.
/// Tables are created on first use.
/// </summary>
public sealed partial class SqliteAuditLog : IAuditLog, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteAuditLog> _logger;
    private bool _initialised;

    public SqliteAuditLog(AuditLogOptions options, ILogger<SqliteAuditLog>? logger = null)
    {
        _connectionString = $"Data Source={options.DatabasePath}";
        _logger = logger ?? NullLogger<SqliteAuditLog>.Instance;
    }

    public async ValueTask LogRetrievalAsync(AuditRetrievalEvent ev, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await EnsureTablesAsync(conn, ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO retrieval_events (request_id, timestamp, caller_roles, chunks, query) " +
                "VALUES ($rid, $ts, $roles, $chunks, $query)";
            cmd.Parameters.AddWithValue("$rid", ev.RequestId);
            cmd.Parameters.AddWithValue("$ts", ev.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("$roles", JsonSerializer.Serialize(ev.CallerRoles));
            cmd.Parameters.AddWithValue("$chunks", JsonSerializer.Serialize(ev.Chunks));
            cmd.Parameters.AddWithValue("$query", (object?)ev.Query ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogWriteFailed(_logger, ex); }
    }

    public async ValueTask LogAnswerAsync(AuditAnswerEvent ev, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await EnsureTablesAsync(conn, ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO answer_events (request_id, timestamp, answer) " +
                "VALUES ($rid, $ts, $answer)";
            cmd.Parameters.AddWithValue("$rid", ev.RequestId);
            cmd.Parameters.AddWithValue("$ts", ev.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("$answer", (object?)ev.Answer ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { LogWriteFailed(_logger, ex); }
    }

    private async ValueTask EnsureTablesAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (_initialised) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS retrieval_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                request_id TEXT NOT NULL,
                timestamp  TEXT NOT NULL,
                caller_roles TEXT NOT NULL,
                chunks     TEXT NOT NULL,
                query      TEXT
            );
            CREATE TABLE IF NOT EXISTS answer_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                request_id TEXT NOT NULL,
                timestamp  TEXT NOT NULL,
                answer     TEXT
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _initialised = true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "SqliteAuditLog failed to write audit event.")]
    private static partial void LogWriteFailed(ILogger logger, Exception ex);
}
```

**Step 5: Add `Microsoft.Data.Sqlite` to `Rag.NET.Security.csproj`**

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="10.*" />
```

**Step 6: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Security.Tests/ --filter "AuditAnswerEngineDecorator|AuditRetrievalBehavior"
dotnet build src/Rag.NET.Security/
```

**Step 7: Commit**

```bash
git add src/Rag.NET.Security/Audit/AuditAnswerEngineDecorator.cs \
        src/Rag.NET.Security/Audit/SqliteAuditLog.cs \
        src/Rag.NET.Security/Rag.NET.Security.csproj \
        tests/Rag.NET.Security.Tests/AuditAnswerEngineDecoratorTests.cs
git commit -m "feat(security): add AuditAnswerEngineDecorator and SqliteAuditLog"
```

---

## Task 8: `Rag.NET.Security.AspNetCore` package

**Files:**
- Create: `src/Rag.NET.Security.AspNetCore/Rag.NET.Security.AspNetCore.csproj`
- Create: `src/Rag.NET.Security.AspNetCore/ClaimsPrincipalCallerContext.cs`
- Create: `src/Rag.NET.Security.AspNetCore/SecurityServiceCollectionExtensions.cs`

**Step 1: Create project file**

```xml
<!-- src/Rag.NET.Security.AspNetCore/Rag.NET.Security.AspNetCore.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.Security.AspNetCore</RootNamespace>
    <PackageId>Rag.NET.Security.AspNetCore</PackageId>
    <Description>ASP.NET Core integration for Rag.NET.Security — binds ICallerContext to ClaimsPrincipal.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.Security\Rag.NET.Security.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Http.Abstractions" Version="2.*" />
  </ItemGroup>
</Project>
```

**Step 2: Add project to solution**

```bash
dotnet sln add src/Rag.NET.Security.AspNetCore/Rag.NET.Security.AspNetCore.csproj
```

**Step 3: Implement**

```csharp
// src/Rag.NET.Security.AspNetCore/ClaimsPrincipalCallerContext.cs
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Rag.NET.Security;

namespace Rag.NET.Security.AspNetCore;

/// <summary>
/// <see cref="ICallerContext"/> that reads roles from the current ASP.NET Core
/// <see cref="ClaimsPrincipal"/> via <see cref="IHttpContextAccessor"/>.
/// Register as a singleton — <see cref="IHttpContextAccessor"/> handles per-request context via AsyncLocal.
/// Returns an empty list when no HTTP context is available (e.g. background jobs).
/// </summary>
public sealed class ClaimsPrincipalCallerContext(IHttpContextAccessor accessor) : ICallerContext
{
    public IReadOnlyList<string> GetRoles() =>
        accessor.HttpContext?.User
            .FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList() ?? [];
}
```

```csharp
// src/Rag.NET.Security.AspNetCore/SecurityServiceCollectionExtensions.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rag.NET.Security;

namespace Rag.NET.Security.AspNetCore;

public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ClaimsPrincipalCallerContext"/> as the <see cref="ICallerContext"/>
    /// implementation. Call this in an ASP.NET Core project after <c>AddRagNet</c>.
    /// </summary>
    public static IServiceCollection AddRagNetAspNetCoreSecurity(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<ICallerContext>(sp =>
            new ClaimsPrincipalCallerContext(sp.GetRequiredService<IHttpContextAccessor>()));
        return services;
    }
}
```

**Step 4: Build to verify**

```bash
dotnet build src/Rag.NET.Security.AspNetCore/
```

**Step 5: Commit**

```bash
git add src/Rag.NET.Security.AspNetCore/
git commit -m "feat(security): add Rag.NET.Security.AspNetCore with ClaimsPrincipalCallerContext"
```

---

## Task 9: `RagBuilderExtensions` — wire everything together

**Files:**
- Modify: `src/Rag.NET.Security/RagBuilderExtensions.cs`

Add `UseRbac`, `UsePiiDetection`, `UseLlmPiiDetection`, and `UseAuditLog` methods:

```csharp
// Append to existing RagBuilderExtensions.cs

public static TBuilder UseRbac<TBuilder>(this TBuilder builder)
    where TBuilder : IRagBuilder
{
    // ICallerContext must be registered separately (e.g. via AddRagNetAspNetCoreSecurity)
    builder.Services.AddSingleton<IRetrievalGuard>(sp =>
        new RbacRetrievalGuard(
            sp.GetRequiredService<ICallerContext>(),
            sp.GetService<ILogger<RbacRetrievalGuard>>()));
    return builder;
}

public static TBuilder UsePiiDetection<TBuilder>(
    this TBuilder builder, Action<PiiDetectionOptions>? configure = null)
    where TBuilder : IRagBuilder
{
    var opts = new PiiDetectionOptions();
    configure?.Invoke(opts);
    builder.Services.AddSingleton(opts);
    builder.Services.AddSingleton<IChunkSanitiser>(sp =>
        new PiiChunkSanitiser(
            sp.GetRequiredService<PiiDetectionOptions>(),
            sp.GetService<ILogger<PiiChunkSanitiser>>()));
    return builder;
}

public static TBuilder UseLlmPiiDetection<TBuilder>(this TBuilder builder)
    where TBuilder : IRagBuilder
{
    builder.Services.AddSingleton<IChunkSanitiser>(sp =>
        new LlmPiiChunkSanitiser(
            sp.GetRequiredService<IChatClient>(),
            sp.GetService<ILogger<LlmPiiChunkSanitiser>>()));
    return builder;
}

public static TBuilder UseAuditLog<TBuilder>(
    this TBuilder builder, Action<AuditLogOptions>? configure = null)
    where TBuilder : IRagBuilder
{
    var opts = new AuditLogOptions();
    configure?.Invoke(opts);
    builder.Services.AddSingleton(opts);
    builder.Services.AddSingleton<IAuditLog>(sp =>
        new SqliteAuditLog(
            sp.GetRequiredService<AuditLogOptions>(),
            sp.GetService<ILogger<SqliteAuditLog>>()));

    // Register AuditRetrievalBehavior and add it to the retrieval pipeline
    builder.Services.AddSingleton<AuditRetrievalBehavior>(sp =>
        new AuditRetrievalBehavior(
            sp.GetRequiredService<IAuditLog>(),
            sp.GetService<ICallerContext>() ?? new AnonymousCallerContext(),
            sp.GetRequiredService<AuditLogOptions>(),
            sp.GetService<ILogger<AuditRetrievalBehavior>>()));

    var pipelineBuilder = builder.Services
        .FirstOrDefault(d => d.ServiceType == typeof(RetrievalPipelineBuilder))
        ?.ImplementationInstance as RetrievalPipelineBuilder;
    pipelineBuilder?.Add<AuditRetrievalBehavior>(after: typeof(RetrievalGuardBehavior));

    // Wire answer engine decorator
    builder.Services.AddSingleton<AuditAnswerEngineDecorator>(sp =>
        new AuditAnswerEngineDecorator(
            sp.GetRequiredService<ChatAnswerEngine>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<AuditLogOptions>(),
            sp.GetService<ILogger<AuditAnswerEngineDecorator>>()));
    builder.Services.AddSingleton<IAnswerEngine>(sp =>
        sp.GetRequiredService<AuditAnswerEngineDecorator>());

    return builder;
}
```

Also add `AnonymousCallerContext` (returns empty roles, used when RBAC is not configured):

```csharp
// src/Rag.NET.Security/AnonymousCallerContext.cs
namespace Rag.NET.Security;

internal sealed class AnonymousCallerContext : ICallerContext
{
    public IReadOnlyList<string> GetRoles() => [];
}
```

**Step 1: Verify build**

```bash
dotnet build src/Rag.NET.Security/
```

**Step 2: Run full security test suite**

```bash
dotnet test tests/Rag.NET.Security.Tests/
```
Expected: all tests pass.

**Step 3: Commit**

```bash
git add src/Rag.NET.Security/RagBuilderExtensions.cs \
        src/Rag.NET.Security/AnonymousCallerContext.cs
git commit -m "feat(security): wire UseRbac, UsePiiDetection, UseLlmPiiDetection, UseAuditLog extensions"
```

---

## Task 10: Update `PipelineBuilderTests` + update docs

**Files:**
- Check: `tests/Rag.NET.Tests/DependencyInjection/PipelineBuilderTests.cs` (update behavior count if `AuditRetrievalBehavior` is added to the default pipeline — it is NOT; it's opt-in, so count stays the same)
- Modify: `docs/guide/retrieval.md` (no retrieval options changes)
- Modify: `docs/reference/features.md` (mark RBAC, PII, Audit as done)

**Step 1: Run full test suite**

```bash
dotnet test tests/Rag.NET.Tests/ tests/Rag.NET.Security.Tests/
```
Expected: all pass. Fix any failures before continuing.

**Step 2: Update `features.md`**

In `docs/reference/features.md`, mark the following as `[x]`:
- `RBAC on Chunks`
- `PII Detection and Redaction`
- `Audit Log`

**Step 3: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark RBAC, PII detection, and audit log as done"
```

---

## Task 11: Final verification

```bash
dotnet build
dotnet test tests/Rag.NET.Tests/ tests/Rag.NET.Security.Tests/
```

All tests must pass with zero warnings.

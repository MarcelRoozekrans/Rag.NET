# C# Semantic Chunking (Roslyn) Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `Rag.NET.Chunking.CSharp` — an `IChunkingStrategy` that uses Roslyn to split C# source files at real AST boundaries (class/method/property/interface etc.), carrying structured metadata per chunk.

**Architecture:** `CSharpChunkingStrategy` implements `IChunkingStrategy`. It parses `DocumentSection.Text` with `CSharpSyntaxTree.ParseText()`, walks the resulting tree with a `CSharpSyntaxWalker`, and yields one `TextChunk` per qualifying member. All C#-specific data (namespace, containing type, member kind, accessibility, XML doc summary) lives in `TextChunk.Metadata` under `csharp.*` keys.

**Tech Stack:** `Microsoft.CodeAnalysis.CSharp` (Roslyn), xUnit v3, NSubstitute, `Rag.NET.Abstractions` project reference.

---

### Task 1: Create the project skeleton

**Files:**
- Create: `src/Rag.NET.Chunking.CSharp/Rag.NET.Chunking.CSharp.csproj`
- Create: `tests/Rag.NET.Chunking.CSharp.Tests/Rag.NET.Chunking.CSharp.Tests.csproj`
- Modify: solution file (add both projects)

**Step 1: Create the src csproj**

```xml
<!-- src/Rag.NET.Chunking.CSharp/Rag.NET.Chunking.CSharp.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Chunking.CSharp</RootNamespace>
    <PackageId>Rag.NET.Chunking.CSharp</PackageId>
    <Description>Roslyn-based C# semantic chunking strategy for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.Chunking.CSharp.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create the test csproj**

```xml
<!-- tests/Rag.NET.Chunking.CSharp.Tests/Rag.NET.Chunking.CSharp.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Chunking.CSharp\Rag.NET.Chunking.CSharp.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>

</Project>
```

**Step 3: Add both projects to the solution**

Run:
```bash
dotnet sln add src/Rag.NET.Chunking.CSharp/Rag.NET.Chunking.CSharp.csproj
dotnet sln add tests/Rag.NET.Chunking.CSharp.Tests/Rag.NET.Chunking.CSharp.Tests.csproj
```

**Step 4: Verify the solution builds**

Run: `dotnet build src/Rag.NET.Chunking.CSharp --configuration Release -v minimal`
Expected: Build succeeded (empty project)

**Step 5: Commit**

```bash
git add src/Rag.NET.Chunking.CSharp/ tests/Rag.NET.Chunking.CSharp.Tests/ *.sln
git commit -m "feat(csharp-chunking): add project skeleton"
```

---

### Task 2: CSharpChunkingOptions

**Files:**
- Create: `src/Rag.NET.Chunking.CSharp/CSharpChunkingOptions.cs`
- Test: `tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingOptionsTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingOptionsTests.cs
using Rag.NET.Chunking.CSharp;
using Xunit;

namespace Rag.NET.Chunking.CSharp.Tests;

public class CSharpChunkingOptionsTests
{
    [Fact]
    public void Defaults_IncludePrivateMembers_IsFalse()
        => Assert.False(new CSharpChunkingOptions().IncludePrivateMembers);

    [Fact]
    public void Defaults_IncludeInternalMembers_IsTrue()
        => Assert.True(new CSharpChunkingOptions().IncludeInternalMembers);

    [Fact]
    public void Defaults_IncludeBodies_IsTrue()
        => Assert.True(new CSharpChunkingOptions().IncludeBodies);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Chunking.CSharp.Tests --filter "CSharpChunkingOptionsTests" -v minimal`
Expected: FAIL — type not found

**Step 3: Implement**

```csharp
// src/Rag.NET.Chunking.CSharp/CSharpChunkingOptions.cs
namespace Rag.NET.Chunking.CSharp;

public sealed class CSharpChunkingOptions
{
    /// <summary>Include private members. Default: false.</summary>
    public bool IncludePrivateMembers { get; init; } = false;

    /// <summary>Include internal members. Default: true.</summary>
    public bool IncludeInternalMembers { get; init; } = true;

    /// <summary>
    /// Include member bodies. When false, only the signature and XML doc comment are included.
    /// Default: true.
    /// </summary>
    public bool IncludeBodies { get; init; } = true;
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Chunking.CSharp.Tests --filter "CSharpChunkingOptionsTests" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add src/Rag.NET.Chunking.CSharp/CSharpChunkingOptions.cs tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingOptionsTests.cs
git commit -m "feat(csharp-chunking): add CSharpChunkingOptions"
```

---

### Task 3: CSharpChunkingStrategy — empty input + parse error fallback

**Files:**
- Create: `src/Rag.NET.Chunking.CSharp/CSharpChunkingStrategy.cs`
- Test: `tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingStrategyTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingStrategyTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.CSharp;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.CSharp.Tests;

public class CSharpChunkingStrategyTests
{
    private static readonly ChunkingOptions DefaultOptions = new();

    private static DocumentSection Section(string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId("test.cs"),
    };

    private static CSharpChunkingStrategy Strategy(CSharpChunkingOptions? opts = null)
        => new(opts ?? new CSharpChunkingOptions(), NullLogger<CSharpChunkingStrategy>.Instance);

    [Fact]
    public async Task ChunkAsync_EmptyInput_ReturnsEmpty()
    {
        var chunks = await Strategy().ChunkAsync(Section(""), DefaultOptions).ToListAsync();
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_WhitespaceInput_ReturnsEmpty()
    {
        var chunks = await Strategy().ChunkAsync(Section("   \n  "), DefaultOptions).ToListAsync();
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkAsync_ParseError_YieldsFallbackChunk()
    {
        // Invalid C# — not a valid compilation unit
        var chunks = await Strategy().ChunkAsync(Section("this is not valid C# @@@"), DefaultOptions).ToListAsync();
        Assert.Single(chunks);
        Assert.Equal("this is not valid C# @@@", chunks[0].Text);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Chunking.CSharp.Tests --filter "CSharpChunkingStrategyTests" -v minimal`
Expected: FAIL — type not found

**Step 3: Write minimal implementation (skeleton + empty/fallback only)**

```csharp
// src/Rag.NET.Chunking.CSharp/CSharpChunkingStrategy.cs
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking.CSharp;

/// <summary>
/// Splits C# source files at AST member boundaries using Roslyn.
/// Each class, interface, method, property, etc. becomes its own <see cref="TextChunk"/>
/// with structured C#-specific metadata.
/// </summary>
public sealed class CSharpChunkingStrategy : IChunkingStrategy
{
    private readonly CSharpChunkingOptions _options;
    private readonly ILogger<CSharpChunkingStrategy> _logger;

    public CSharpChunkingStrategy(CSharpChunkingOptions options, ILogger<CSharpChunkingStrategy> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(section.Text))
            yield break;

        var tree = CSharpSyntaxTree.ParseText(section.Text, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        // If there are any errors, fall back to a single chunk with the raw text
        if (root.ContainsDiagnostics && root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            _logger.LogWarning("C# parse errors in document {DocumentId}; falling back to single chunk", section.DocumentId);
            yield return new TextChunk
            {
                Text = section.Text,
                DocumentId = section.DocumentId,
                ChunkIndex = 0,
            };
            yield break;
        }

        // Full implementation in next task
        await foreach (var chunk in ExtractMembersAsync(root, section, options, cancellationToken))
            yield return chunk;
    }

    private static async IAsyncEnumerable<TextChunk> ExtractMembersAsync(
        SyntaxNode root,
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false); // async placeholder
        yield break;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Chunking.CSharp.Tests --filter "CSharpChunkingStrategyTests" -v minimal`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add src/Rag.NET.Chunking.CSharp/CSharpChunkingStrategy.cs tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingStrategyTests.cs
git commit -m "feat(csharp-chunking): add CSharpChunkingStrategy skeleton with empty/fallback handling"
```

---

### Task 4: Member extraction — simple class

**Files:**
- Modify: `src/Rag.NET.Chunking.CSharp/CSharpChunkingStrategy.cs`
- Modify: `tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingStrategyTests.cs`

**Step 1: Write the failing tests** (add to existing test class)

```csharp
[Fact]
public async Task ChunkAsync_SimpleClass_YieldsOneChunkPerMember()
{
    const string source = """
        namespace MyApp;

        public class Calculator
        {
            public int Add(int a, int b) => a + b;

            public string Name { get; set; } = "calc";
        }
        """;

    var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions).ToListAsync();

    // Should yield: the class itself, plus the method, plus the property = 3 chunks
    // OR just method + property = 2 chunks depending on design
    // Design: all member declaration nodes including the class → 3 chunks
    Assert.Equal(3, chunks.Count);
}

[Fact]
public async Task ChunkAsync_SimpleClass_MetadataKeys_CorrectNamespaceAndKind()
{
    const string source = """
        namespace MyApp.Core;

        public class Greeter
        {
            public string Greet(string name) => $"Hello {name}";
        }
        """;

    var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions).ToListAsync();
    var methodChunk = chunks.Single(c => c.Metadata.TryGetValue("csharp.kind", out var k) && k == "method");

    Assert.Equal("MyApp.Core", methodChunk.Metadata["csharp.namespace"]);
    Assert.Equal("Greeter", methodChunk.Metadata["csharp.type"]);
    Assert.Equal("Greet", methodChunk.Metadata["csharp.name"]);
    Assert.Equal("method", methodChunk.Metadata["csharp.kind"]);
    Assert.Equal("public", methodChunk.Metadata["csharp.accessibility"]);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Chunking.CSharp.Tests --filter "YieldsOneChunkPerMember|MetadataKeys" -v minimal`
Expected: FAIL

**Step 3: Implement member extraction**

Replace `ExtractMembersAsync` in `CSharpChunkingStrategy.cs` with a real implementation:

```csharp
private async IAsyncEnumerable<TextChunk> ExtractMembersAsync(
    SyntaxNode root,
    DocumentSection section,
    ChunkingOptions options,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    await Task.CompletedTask.ConfigureAwait(false);

    int chunkIndex = 0;
    var walker = new MemberWalker(_options);
    walker.Visit(root);

    foreach (var (node, metadata) in walker.Members)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = _options.IncludeBodies
            ? node.ToFullString().Trim()
            : ExtractSignatureAndDoc(node);

        if (string.IsNullOrWhiteSpace(text))
            continue;

        var chunkMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in metadata)
            chunkMetadata[kv.Key] = kv.Value;

        if (text.Length > options.MaxChunkSize)
            chunkMetadata["csharp.oversized"] = "true";

        yield return new TextChunk
        {
            Text = text,
            DocumentId = section.DocumentId,
            ChunkIndex = chunkIndex++,
            Metadata = chunkMetadata,
        };
    }
}

private static string ExtractSignatureAndDoc(SyntaxNode node)
{
    // Strip the body from method/property/constructor nodes; keep everything else
    return node switch
    {
        MethodDeclarationSyntax m => StripBody(m, m.Body, m.ExpressionBody),
        ConstructorDeclarationSyntax c => StripBody(c, c.Body, c.ExpressionBody),
        PropertyDeclarationSyntax p => StripBody(p, p.AccessorList, p.ExpressionBody),
        _ => node.ToFullString().Trim(),
    };
}

private static string StripBody(SyntaxNode node, SyntaxNode? body1, SyntaxNode? body2)
{
    var text = node.ToFullString();
    foreach (var body in new[] { body1, body2 })
    {
        if (body is null) continue;
        var bodyText = body.ToFullString();
        var idx = text.IndexOf(bodyText, StringComparison.Ordinal);
        if (idx >= 0)
            text = text[..idx].TrimEnd() + ";";
    }
    return text.Trim();
}
```

Also add the `MemberWalker` nested class at the bottom of the file:

```csharp
private sealed class MemberWalker : CSharpSyntaxWalker
{
    private readonly CSharpChunkingOptions _options;
    public List<(SyntaxNode Node, Dictionary<string, string> Metadata)> Members { get; } = [];

    public MemberWalker(CSharpChunkingOptions options) => _options = options;

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        AddIfQualifies(node, "class");
        base.VisitClassDeclaration(node); // recurse into nested types/members
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        AddIfQualifies(node, "interface");
        base.VisitInterfaceDeclaration(node);
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        AddIfQualifies(node, "record");
        base.VisitRecordDeclaration(node);
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        AddIfQualifies(node, "struct");
        base.VisitStructDeclaration(node);
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        AddIfQualifies(node, "enum");
        // Don't recurse — enum members are not chunked individually
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        AddIfQualifies(node, "method");
        // Don't recurse into method bodies
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        AddIfQualifies(node, "constructor");
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        AddIfQualifies(node, "property");
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        AddIfQualifies(node, "delegate");
    }

    public override void VisitEventDeclaration(EventDeclarationSyntax node)
    {
        AddIfQualifies(node, "event");
    }

    private void AddIfQualifies(MemberDeclarationSyntax node, string kind)
    {
        var accessibility = GetAccessibility(node);
        if (accessibility == "private" && !_options.IncludePrivateMembers) return;
        if (accessibility == "internal" && !_options.IncludeInternalMembers) return;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["csharp.kind"] = kind,
            ["csharp.namespace"] = GetNamespace(node),
            ["csharp.type"] = GetContainingType(node),
            ["csharp.name"] = GetName(node),
            ["csharp.accessibility"] = accessibility,
            ["csharp.summary"] = GetXmlDocSummary(node),
        };

        Members.Add((node, metadata));
    }

    private static string GetAccessibility(MemberDeclarationSyntax node)
    {
        var modifiers = node.Modifiers;
        bool isPublic    = modifiers.Any(SyntaxKind.PublicKeyword);
        bool isProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        bool isInternal  = modifiers.Any(SyntaxKind.InternalKeyword);
        bool isPrivate   = modifiers.Any(SyntaxKind.PrivateKeyword);

        if (isPublic) return "public";
        if (isProtected && isInternal) return "protected internal";
        if (isPrivate && isProtected) return "private protected";
        if (isProtected) return "protected";
        if (isInternal) return "internal";
        if (isPrivate) return "private";

        // Default accessibility: private for type members, internal for top-level types
        return node.Parent is TypeDeclarationSyntax ? "private" : "internal";
    }

    private static string GetNamespace(SyntaxNode node)
    {
        var ancestor = node.Parent;
        while (ancestor is not null)
        {
            if (ancestor is FileScopedNamespaceDeclarationSyntax fsn)
                return fsn.Name.ToString();
            if (ancestor is NamespaceDeclarationSyntax ns)
                return ns.Name.ToString();
            ancestor = ancestor.Parent;
        }
        return string.Empty;
    }

    private static string GetContainingType(SyntaxNode node)
    {
        var parent = node.Parent;
        while (parent is not null)
        {
            if (parent is TypeDeclarationSyntax t && parent != node)
                return t.Identifier.Text;
            parent = parent.Parent;
        }
        return string.Empty;
    }

    private static string GetName(MemberDeclarationSyntax node) => node switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        MethodDeclarationSyntax m   => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        DelegateDeclarationSyntax d => d.Identifier.Text,
        EventDeclarationSyntax e    => e.Identifier.Text,
        _                           => string.Empty,
    };

    private static string GetXmlDocSummary(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                              || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

        if (trivia == default)
            return string.Empty;

        var xml = trivia.ToString();

        // Extract text between <summary> and </summary>, strip /// prefixes and tags
        var start = xml.IndexOf("<summary>", StringComparison.OrdinalIgnoreCase);
        var end   = xml.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0 || end <= start)
            return string.Empty;

        var raw = xml[(start + "<summary>".Length)..end];

        // Strip /// prefixes line by line
        var lines = raw.Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart().TrimStart('/').Trim();
            if (trimmed.Length > 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(trimmed);
            }
        }

        return sb.ToString();
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Chunking.CSharp.Tests -v minimal`
Expected: all pass

**Step 5: Commit**

```bash
git add src/Rag.NET.Chunking.CSharp/CSharpChunkingStrategy.cs tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingStrategyTests.cs
git commit -m "feat(csharp-chunking): implement member extraction with metadata"
```

---

### Task 5: Accessibility filtering + XML doc + nested types + oversized

**Files:**
- Modify: `tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingStrategyTests.cs`

**Step 1: Add the remaining test facts**

```csharp
[Fact]
public async Task ChunkAsync_PrivateMember_ExcludedByDefault()
{
    const string source = """
        public class Foo
        {
            public void Public() { }
            private void Private() { }
        }
        """;

    var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions).ToListAsync();
    Assert.DoesNotContain(chunks, c =>
        c.Metadata.TryGetValue("csharp.name", out var n) && n == "Private");
}

[Fact]
public async Task ChunkAsync_PrivateMember_IncludedWhenOptionSet()
{
    const string source = """
        public class Foo
        {
            public void Public() { }
            private void Private() { }
        }
        """;

    var chunks = await Strategy(new CSharpChunkingOptions { IncludePrivateMembers = true })
        .ChunkAsync(Section(source), DefaultOptions).ToListAsync();

    Assert.Contains(chunks, c =>
        c.Metadata.TryGetValue("csharp.name", out var n) && n == "Private");
}

[Fact]
public async Task ChunkAsync_XmlDoc_ExtractedToSummaryMetadata()
{
    const string source = """
        public class Greeter
        {
            /// <summary>Says hello to the given name.</summary>
            public string Greet(string name) => $"Hello {name}";
        }
        """;

    var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions).ToListAsync();
    var greet = chunks.Single(c =>
        c.Metadata.TryGetValue("csharp.name", out var n) && n == "Greet");

    Assert.Equal("Says hello to the given name.", greet.Metadata["csharp.summary"]);
}

[Fact]
public async Task ChunkAsync_NestedClass_YieldsOuterAndInnerSeparately()
{
    const string source = """
        public class Outer
        {
            public class Inner
            {
                public void InnerMethod() { }
            }
        }
        """;

    var chunks = await Strategy().ChunkAsync(Section(source), DefaultOptions).ToListAsync();
    var kinds = chunks.Select(c => c.Metadata["csharp.name"]).ToList();

    Assert.Contains("Outer", kinds);
    Assert.Contains("Inner", kinds);
    Assert.Contains("InnerMethod", kinds);
}

[Fact]
public async Task ChunkAsync_OversizedMember_YieldsWithOversizedFlag()
{
    // Large method body
    var body = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"    var x{i} = {i};"));
    var source = $$"""
        public class Big
        {
            public void HugeMethod()
            {
        {{body}}
            }
        }
        """;

    var tinyOptions = new ChunkingOptions { MaxChunkSize = 50 };
    var chunks = await Strategy().ChunkAsync(Section(source), tinyOptions).ToListAsync();
    var huge = chunks.Single(c =>
        c.Metadata.TryGetValue("csharp.name", out var n) && n == "HugeMethod");

    Assert.Equal("true", huge.Metadata["csharp.oversized"]);
}
```

**Step 2: Run tests to verify they pass (no implementation change needed)**

Run: `dotnet test tests/Rag.NET.Chunking.CSharp.Tests -v minimal`
Expected: all pass

**Step 3: Commit**

```bash
git add tests/Rag.NET.Chunking.CSharp.Tests/CSharpChunkingStrategyTests.cs
git commit -m "test(csharp-chunking): add accessibility, XML doc, nested, oversized tests"
```

---

### Task 6: RagBuilderExtensions + DI test

**Files:**
- Create: `src/Rag.NET.Chunking.CSharp/RagBuilderExtensions.cs`
- Modify: `tests/Rag.NET.Tests/DependencyInjection/UseCSharpChunkingTests.cs` (create new)

**Step 1: Write the failing DI test**

```csharp
// tests/Rag.NET.Tests/DependencyInjection/UseCSharpChunkingTests.cs
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking.CSharp;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseCSharpChunkingTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton(Substitute.For<IVectorStore>());
        return services;
    }

    [Fact]
    public void UseCSharpChunking_RegistersIChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseCSharpChunking()).BuildServiceProvider();
        Assert.IsType<CSharpChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseCSharpChunking_DefaultOptions_IncludePrivateIsFalse()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseCSharpChunking()).BuildServiceProvider();
        Assert.False(sp.GetRequiredService<CSharpChunkingOptions>().IncludePrivateMembers);
    }

    [Fact]
    public void UseCSharpChunking_CustomOptions_Applied()
    {
        var sp = BaseServices()
            .AddRagNet(rag => rag.UseCSharpChunking(o => o = new CSharpChunkingOptions { IncludePrivateMembers = true }))
            .BuildServiceProvider();
        Assert.False(sp.GetRequiredService<CSharpChunkingOptions>().IncludePrivateMembers); // still false — configure action mutates
    }
}
```

> **Note:** `CSharpChunkingOptions` uses `init` properties so the configure pattern should accept an `Action<CSharpChunkingOptions>` only if the options are mutable setters. Since `init` is used, the registration should accept a `CSharpChunkingOptions?` parameter directly (like `UseHierarchicalMerging`), not an `Action<>`. Write the third test accordingly — pass a pre-built options object:

```csharp
[Fact]
public void UseCSharpChunking_CustomOptions_Applied()
{
    var opts = new CSharpChunkingOptions { IncludePrivateMembers = true };
    var sp = BaseServices()
        .AddRagNet(rag => rag.UseCSharpChunking(opts))
        .BuildServiceProvider();
    Assert.True(sp.GetRequiredService<CSharpChunkingOptions>().IncludePrivateMembers);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseCSharpChunkingTests" -v minimal`
Expected: FAIL

**Step 3: Implement RagBuilderExtensions**

```csharp
// src/Rag.NET.Chunking.CSharp/RagBuilderExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;

namespace Rag.NET.Chunking.CSharp;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="CSharpChunkingStrategy"/> as <see cref="IChunkingStrategy"/>.
    /// Uses Roslyn to split C# source files at AST member boundaries (class, method, property, etc.),
    /// carrying structured metadata per chunk.
    /// </summary>
    public static TBuilder UseCSharpChunking<TBuilder>(this TBuilder builder, CSharpChunkingOptions? options = null)
        where TBuilder : IRagBuilder
    {
        var opts = options ?? new CSharpChunkingOptions();
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<IChunkingStrategy>(sp =>
            new CSharpChunkingStrategy(opts, sp.GetRequiredService<ILogger<CSharpChunkingStrategy>>()));
        return builder;
    }
}
```

**Step 4: Add `Rag.NET.Chunking.CSharp` reference to `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`**

Add:
```xml
<ProjectReference Include="..\..\src\Rag.NET.Chunking.CSharp\Rag.NET.Chunking.CSharp.csproj" />
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests --filter "UseCSharpChunkingTests" -v minimal`
Expected: PASS

**Step 6: Commit**

```bash
git add src/Rag.NET.Chunking.CSharp/RagBuilderExtensions.cs tests/Rag.NET.Tests/DependencyInjection/UseCSharpChunkingTests.cs tests/Rag.NET.Tests/Rag.NET.Tests.csproj
git commit -m "feat(csharp-chunking): add DI registration + UseCSharpChunking extension"
```

---

### Task 7: Full solution build + all tests green

**Step 1: Build everything**

Run: `dotnet build --configuration Release -v minimal`
Expected: Build succeeded, 0 errors

**Step 2: Run all tests**

Run: `dotnet test --configuration Release -v minimal`
Expected: all pass

**Step 3: Fix any build or test failures before proceeding**

**Step 4: Commit any fixups needed**

---

### Task 8: Update docs/reference/features.md backlog

**Files:**
- Modify: `docs/reference/features.md`

**Step 1: Find the C# Semantic Chunking entry in features.md and update its Status**

Find the line:
```
| [ ] | C# Semantic Chunking (Roslyn) | High | `Microsoft.CodeAnalysis.CSharp` |
```

Change to:
```
| [x] | C# Semantic Chunking (Roslyn) | High | `Microsoft.CodeAnalysis.CSharp` |
```

Also find the backlog description section for "C# Semantic Chunking (Roslyn)" and add `**Status:** ✅ Done` below the description.

**Step 2: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark C# Semantic Chunking as done in feature backlog"
```

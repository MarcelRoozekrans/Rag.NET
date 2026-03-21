# ZeroAlloc.ValueObjects Adoption Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace 6 hand-written equality members on `DocumentId` with `[ValueObject]` source generation, and fix `DocumentSection.DocumentId` from raw `string` to the typed `DocumentId` wrapper.

**Architecture:** `ZeroAlloc.ValueObjects` source-generates `Equals`, `GetHashCode`, `==`, `!=`, and `ToString` for any `partial class` annotated with `[ValueObject]`. `[EqualityMember]` opts in specific fields/properties when the type uses a private backing field. After adding the package, `DocumentId` sheds its equality boilerplate; fixing `DocumentSection` makes the type consistent with `TextChunk` and removes three awkward `new DocumentId(section.DocumentId)` wrapping sites in the chunking strategies.

**Tech Stack:** .NET 10, C# 13, ZeroAlloc.ValueObjects 1.x (NuGet), xUnit

---

### Task 1: Add ZeroAlloc.ValueObjects package reference

**Files:**
- Modify: `src/Rag.NET/Rag.NET.csproj`

**Context:** The project registers all Roslyn generators explicitly via `<Analyzer>` includes rather than relying on automatic pickup. Follow that convention for `ZeroAlloc.ValueObjects`. The generator DLL is bundled in the main package under `analyzers/dotnet/cs/`.

**Step 1: Add the PackageReference**

In `src/Rag.NET/Rag.NET.csproj`, add to the existing `<ItemGroup>` that contains the other `ZeroAlloc.*` references (around line 24):

```xml
<PackageReference Include="ZeroAlloc.ValueObjects" Version="1.*" GeneratePathProperty="true" />
```

**Step 2: Add the Analyzer include**

In `src/Rag.NET/Rag.NET.csproj`, add to the existing `<ItemGroup>` that contains the other `<Analyzer>` entries (around line 35):

```xml
<Analyzer Include="$(PkgZeroAlloc_ValueObjects)\analyzers\dotnet\cs\ZeroAlloc.ValueObjects.Generator.dll" />
```

**Step 3: Restore packages and verify build**

Run:
```bash
cd src/Rag.NET && dotnet build
```

Expected: Build succeeds with no errors. The package is restored and the generator is wired up.

**Step 4: Commit**

```bash
git add src/Rag.NET/Rag.NET.csproj
git commit -m "chore: add ZeroAlloc.ValueObjects package reference"
```

---

### Task 2: Refactor DocumentId to use [ValueObject]

**Files:**
- Modify: `src/Rag.NET/Models/DocumentId.cs`
- Test: `tests/Rag.NET.Tests/Models/DocumentIdTests.cs` (run, do not modify)

**Context:** `DocumentId` is a `sealed class` with a private `string _value` field. The generator selects public properties by default — since `_value` is a private field, add `[EqualityMember]` to opt it in. The custom `ToString()` (returns `_value` bare) and the nested `DocumentIdJsonConverter` must stay. The generated equality semantics match the hand-written ones: `string.Equals` is ordinal for `==`, and `string.GetHashCode()` is ordinal.

**Step 1: Run the existing tests to confirm green baseline**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~DocumentIdTests" --no-build
```

Expected: 10 tests pass. (Build first if needed: remove `--no-build`.)

**Step 2: Rewrite DocumentId.cs**

Replace the entire file `src/Rag.NET/Models/DocumentId.cs` with:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroAlloc.ValueObjects;

namespace Rag.NET.Models;

[JsonConverter(typeof(DocumentIdJsonConverter))]
[ValueObject]
public sealed partial class DocumentId
{
    [EqualityMember]
    private readonly string _value;

    public DocumentId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public override string ToString() => _value;

    public static implicit operator string(DocumentId id) => id._value;
    public static explicit operator DocumentId(string s) => new(s);

    private sealed class DocumentIdJsonConverter : JsonConverter<DocumentId>
    {
        public override DocumentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (string.IsNullOrEmpty(value))
                throw new JsonException("DocumentId cannot be null or empty.");
            return new(value);
        }

        public override void Write(Utf8JsonWriter writer, DocumentId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value._value);
    }
}
```

Key changes vs the original:
- `partial` added to class declaration
- `[ValueObject]` attribute added
- `[EqualityMember]` attribute added on `_value`
- `IEquatable<DocumentId>` removed from base list
- `Equals(DocumentId?)`, `Equals(object?)`, `GetHashCode()`, `operator ==`, `operator !=` deleted
- `using ZeroAlloc.ValueObjects;` added

**Step 3: Build**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```

Expected: Builds with no errors. The generator emits the equality members into a second partial class file at compile time.

**Step 4: Run DocumentId tests**

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FullyQualifiedName~DocumentIdTests"
```

Expected: All 10 tests pass. If any fail, compare the generated equality semantics — the most likely issue is `ToString` format (the generated `ToString` should be suppressed because the class already defines it, but if it isn't, the `DocumentId_ToStringReturnsValue` test will catch it).

**Step 5: Commit**

```bash
git add src/Rag.NET/Models/DocumentId.cs
git commit -m "refactor: replace DocumentId equality boilerplate with [ValueObject]"
```

---

### Task 3: Fix DocumentSection.DocumentId type

**Files:**
- Modify: `src/Rag.NET/Models/DocumentSection.cs`

**Context:** `DocumentSection.DocumentId` is currently `string` while `TextChunk.DocumentId` is typed `DocumentId`. This inconsistency forces chunking strategies to wrap it: `new DocumentId(section.DocumentId)`. The `implicit operator string` on `DocumentId` means all **read** sites compile without changes — only **construction** sites that pass a raw `string` need updating (done in Tasks 4 and 5).

**Step 1: Change the property type**

In `src/Rag.NET/Models/DocumentSection.cs`, change line 6:

```csharp
// Before
public required string DocumentId { get; init; }

// After
public required DocumentId DocumentId { get; init; }
```

Full file after change:

```csharp
namespace Rag.NET.Models;

public sealed record DocumentSection
{
    public required string Text { get; init; }
    public required DocumentId DocumentId { get; init; }
    public int? HeadingLevel { get; init; }
    public string? Heading { get; init; }
    public int? PageNumber { get; init; }
    public int SectionIndex { get; init; }
}
```

**Step 2: Verify the expected build errors**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```

Expected: Build **fails** with errors at:
- `Chunking/FixedSizeChunkingStrategy.cs` — `new DocumentId(section.DocumentId)` (can't pass `DocumentId` to `DocumentId(string)`)
- `Chunking/TokenAwareChunkingStrategy.cs` — same
- `Chunking/RecursiveChunkingStrategy.cs` — same
- `Parsers/MarkdownDocumentParser.cs` — `string documentId` parameter mismatch
- `Parsers.Html/HtmlDocumentParser.cs` — same

These errors are the expected signal that call sites need updating. Do not commit yet — continue to Tasks 4 and 5.

---

### Task 4: Update chunking strategies

**Files:**
- Modify: `src/Rag.NET/Chunking/FixedSizeChunkingStrategy.cs:47`
- Modify: `src/Rag.NET/Chunking/TokenAwareChunkingStrategy.cs:86`
- Modify: `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs:60`

**Context:** All three chunking strategies currently do `DocumentId = new DocumentId(section.DocumentId)` to convert the raw string to the typed wrapper. Now that `section.DocumentId` is already `DocumentId`, just assign it directly.

**Step 1: Fix FixedSizeChunkingStrategy.cs**

At line 47, change:
```csharp
// Before
DocumentId = new DocumentId(section.DocumentId),

// After
DocumentId = section.DocumentId,
```

**Step 2: Fix TokenAwareChunkingStrategy.cs**

At line 86, change:
```csharp
// Before
DocumentId = new DocumentId(section.DocumentId),

// After
DocumentId = section.DocumentId,
```

**Step 3: Fix RecursiveChunkingStrategy.cs**

At line 60, change:
```csharp
// Before
DocumentId = new DocumentId(section.DocumentId),

// After
DocumentId = section.DocumentId,
```

**Step 4: Partial build check**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```

Expected: Chunking errors are gone; parser errors remain. Continue to Task 5.

---

### Task 5: Update parser helper methods

**Files:**
- Modify: `src/Rag.NET/Parsers/MarkdownDocumentParser.cs:59,68`
- Modify: `src/Rag.NET.Parsers.Html/HtmlDocumentParser.cs:88,122`

**Context:** `MarkdownDocumentParser` and `HtmlDocumentParser` have private helper methods that accept `string documentId` and assign it to `DocumentSection.DocumentId`. Now that the property is typed `DocumentId`, the parameter type must match. The callers pass `metadata.DocumentId` (already `DocumentId`), so no changes are needed at the call sites — only the method signatures and the internal property assignment.

**Step 1: Fix MarkdownDocumentParser.cs**

Change the two private method signatures (lines 59 and 68):

```csharp
// Before — line 59
private static DocumentSection CreatePlainSection(string text, string documentId, int index) =>

// After
private static DocumentSection CreatePlainSection(string text, DocumentId documentId, int index) =>
```

```csharp
// Before — line 68
private static DocumentSection CreateHeadingSection(
    string text, MatchCollection matches, int i, string documentId, int sectionIndex)

// After
private static DocumentSection CreateHeadingSection(
    string text, MatchCollection matches, int i, DocumentId documentId, int sectionIndex)
```

The body of both methods assigns `DocumentId = documentId` — no change needed there since the type now matches.

**Step 2: Fix HtmlDocumentParser.cs**

Change the two private method signatures (lines 88 and 122):

```csharp
// Before — line 88
private static DocumentSection? BuildHeadingSection(IElement heading, string documentId, int sectionIndex)

// After
private static DocumentSection? BuildHeadingSection(IElement heading, DocumentId documentId, int sectionIndex)
```

```csharp
// Before — line 122
private static DocumentSection CreateSection(string text, string documentId, int sectionIndex) =>

// After
private static DocumentSection CreateSection(string text, DocumentId documentId, int sectionIndex) =>
```

**Step 3: Build the full solution**

```bash
dotnet build
```

Expected: Full solution builds with no errors.

**Step 4: Run full test suite**

```bash
dotnet test
```

Expected: All tests pass. The 10 `DocumentIdTests` are the key regression check; any failure points to an equality semantics mismatch in the generated code.

**Step 5: Commit**

```bash
git add src/Rag.NET/Models/DocumentSection.cs \
        src/Rag.NET/Chunking/FixedSizeChunkingStrategy.cs \
        src/Rag.NET/Chunking/TokenAwareChunkingStrategy.cs \
        src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs \
        src/Rag.NET/Parsers/MarkdownDocumentParser.cs \
        src/Rag.NET.Parsers.Html/HtmlDocumentParser.cs
git commit -m "fix: change DocumentSection.DocumentId from string to typed DocumentId"
```

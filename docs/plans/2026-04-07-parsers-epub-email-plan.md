# EPUB and Email (EML) Parsers — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add two new `IDocumentParser` implementations — one for EPUB e-books and one for EML email files — each in its own NuGet package, following existing parser conventions exactly.

**Architecture:** Both parsers implement `IDocumentParser` from `Rag.NET.Abstractions`, register via an `IRagBuilder` extension method, and live in their own `src/Rag.NET.Parsers.*` project. The EPUB parser delegates chapter HTML to `HtmlDocumentParser`. The Email parser dispatches attachments to whichever registered `IDocumentParser` handles that content type (same pattern as `ParseBehavior`).

**Tech Stack:** `VersOne.Epub` (MIT) for EPUB reading; `MimeKit` (MIT) for EML parsing; `AngleSharp` (already a dep via `HtmlDocumentParser`) for HTML body fallback; `xunit.v3` + `TestContext.Current.CancellationToken` for tests.

---

## Context for the implementer

### Key conventions to follow

- `DocumentSection` is a `sealed record` with `init` properties — use `with { SectionIndex = n }` to re-stamp indices.
- Every parser is `sealed`, takes constructor parameters via primary constructor syntax, implements `IDocumentParser`.
- Builder extensions use `IRagBuilder` (from `Rag.NET.Abstractions`), generic `TBuilder where TBuilder : IRagBuilder`, return `TBuilder`.
- Test files live in `tests/Rag.NET.Tests/Parsers/`. Use `TestContext.Current.CancellationToken`. Call `.ToListAsync(ct)` to collect async enumerables.
- The solution file is `Rag.NET.slnx` (XML format). Add new projects as `<Project Path="src/..."/>` inside the `<Folder Name="/src/">` element.
- Tests project is `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` — add `<ProjectReference>` entries there for new parser projects.

### Existing parser to mirror: `Rag.NET.Parsers.Html`

- `src/Rag.NET.Parsers.Html/Rag.NET.Parsers.Html.csproj` — references `Rag.NET.Abstractions` + `AngleSharp`
- `src/Rag.NET.Parsers.Html/HtmlDocumentParser.cs` — `sealed`, no constructor args, `CanParse("text/html")`
- `src/Rag.NET.Parsers.Html/HtmlParserBuilderExtensions.cs` — `AddHtmlParser<TBuilder>(this TBuilder builder)` calls `builder.AddParser<HtmlDocumentParser>()`

### `IDocumentParser` interface (Rag.NET.Abstractions)

```csharp
public interface IDocumentParser
{
    bool CanParse(string contentType);
    IAsyncEnumerable<DocumentSection> ParseAsync(Stream stream, DocumentMetadata metadata, CancellationToken cancellationToken = default);
}
```

### `DocumentSection` record

```csharp
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

### `IRagBuilder.AddParser<TParser>()`

```csharp
IRagBuilder AddParser<TParser>() where TParser : class, IDocumentParser;
```

---

## Task 1: EPUB parser project scaffold

**Files:**
- Create: `src/Rag.NET.Parsers.Epub/Rag.NET.Parsers.Epub.csproj`
- Modify: `Rag.NET.slnx`
- Modify: `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`

**Step 1: Create the project file**

`src/Rag.NET.Parsers.Epub/Rag.NET.Parsers.Epub.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Parsers.Epub</RootNamespace>
    <PackageId>Rag.NET.Parsers.Epub</PackageId>
    <Description>EPUB document parser for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />
    <ProjectReference Include="..\Rag.NET.Parsers.Html\Rag.NET.Parsers.Html.csproj" />
    <PackageReference Include="VersOne.Epub" Version="4.*" />
  </ItemGroup>

</Project>
```

**Step 2: Add to solution**

In `Rag.NET.slnx`, inside `<Folder Name="/src/">`, add after the `Rag.NET.Parsers.Html` entry:
```xml
    <Project Path="src/Rag.NET.Parsers.Epub/Rag.NET.Parsers.Epub.csproj" />
```

**Step 3: Add project reference to tests**

In `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`, add inside `<ItemGroup>`:
```xml
    <ProjectReference Include="..\..\src\Rag.NET.Parsers.Epub\Rag.NET.Parsers.Epub.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET.Parsers.Html\Rag.NET.Parsers.Html.csproj" />
```

**Step 4: Verify it builds**

Run: `dotnet build src/Rag.NET.Parsers.Epub/Rag.NET.Parsers.Epub.csproj`
Expected: Build succeeded (0 errors)

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.Epub/ Rag.NET.slnx tests/Rag.NET.Tests/Rag.NET.Tests.csproj
git commit -m "chore: scaffold Rag.NET.Parsers.Epub project"
```

---

## Task 2: EPUB parser — tests first

**Files:**
- Create: `tests/Rag.NET.Tests/Parsers/EpubDocumentParserTests.cs`

**Step 1: Write failing tests**

`tests/Rag.NET.Tests/Parsers/EpubDocumentParserTests.cs`:
```csharp
using Rag.NET.Models;
using Rag.NET.Parsers.Epub;
using Rag.NET.Parsers.Html;
using VersOne.Epub;
using VersOne.Epub.Schema;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class EpubDocumentParserTests
{
    private readonly HtmlDocumentParser _htmlParser = new();
    private EpubDocumentParser CreateSut() => new(_htmlParser);

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("epub-1"),
        FileName = "test.epub",
        ContentType = "application/epub+zip",
    };

    [Fact]
    public void CanParse_EpubMimeType_ReturnsTrue()
    {
        Assert.True(CreateSut().CanParse("application/epub+zip"));
    }

    [Fact]
    public void CanParse_OtherType_ReturnsFalse()
    {
        Assert.False(CreateSut().CanParse("text/plain"));
    }

    [Fact]
    public async Task ParseAsync_SingleChapterWithHeadings_YieldsSectionsPerHeading()
    {
        // Build an in-memory EPUB with one chapter containing two headings
        var html = "<html><body><h1>Chapter 1</h1><p>First paragraph.</p><h2>Section A</h2><p>Body A.</p></body></html>";
        using var stream = BuildEpub([html]);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Chapter 1", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Section A", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_MultipleChapters_SectionIndicesAreGlobal()
    {
        var chapter1 = "<html><body><h1>Chapter 1</h1><p>Text 1.</p></body></html>";
        var chapter2 = "<html><body><h1>Chapter 2</h1><p>Text 2.</p></body></html>";
        using var stream = BuildEpub([chapter1, chapter2]);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    [Fact]
    public async Task ParseAsync_EmptyChapters_YieldsNoSections()
    {
        using var stream = BuildEpub(["<html><body></body></html>"]);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_AllSectionsCarryDocumentId()
    {
        var html = "<html><body><h1>Title</h1><p>Text.</p></body></html>";
        using var stream = BuildEpub([html]);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("epub-1", s.DocumentId));
    }

    // Builds a minimal valid EPUB stream with the given HTML chapters as spine items.
    private static MemoryStream BuildEpub(string[] chapterHtmls)
    {
        var epubBuilder = EpubBuilder.CreateBook("Test Book");
        epubBuilder.AddAuthor("Test Author");
        foreach (var (html, i) in chapterHtmls.Select((h, i) => (h, i)))
        {
            epubBuilder.AddChapter($"Chapter {i + 1}", html);
        }
        var ms = new MemoryStream();
        epubBuilder.WriteToStream(ms);
        ms.Position = 0;
        return ms;
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "EpubDocumentParserTests" -v q`
Expected: Compilation error — `EpubDocumentParser` does not exist yet.

**Step 3: Commit tests**

```bash
git add tests/Rag.NET.Tests/Parsers/EpubDocumentParserTests.cs
git commit -m "test(epub): add failing tests for EpubDocumentParser"
```

---

## Task 3: EPUB parser — implementation

**Files:**
- Create: `src/Rag.NET.Parsers.Epub/EpubDocumentParser.cs`
- Create: `src/Rag.NET.Parsers.Epub/EpubParserBuilderExtensions.cs`

**Step 1: Write the parser**

`src/Rag.NET.Parsers.Epub/EpubDocumentParser.cs`:
```csharp
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;
using VersOne.Epub;

namespace Rag.NET.Parsers.Epub;

public sealed class EpubDocumentParser(HtmlDocumentParser htmlParser) : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("application/epub+zip", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var book = await EpubReader.OpenBookAsync(stream).ConfigureAwait(false);
        int sectionIndex = 0;

        foreach (var item in book.ReadingOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var html = item.Content;
            if (string.IsNullOrWhiteSpace(html))
                continue;

            using var chapterStream = new MemoryStream(Encoding.UTF8.GetBytes(html));
            await foreach (var section in htmlParser.ParseAsync(chapterStream, metadata, cancellationToken).ConfigureAwait(false))
            {
                yield return section with { SectionIndex = sectionIndex++ };
            }
        }
    }
}
```

**Step 2: Write the builder extension**

`src/Rag.NET.Parsers.Epub/EpubParserBuilderExtensions.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Epub;

public static class EpubParserBuilderExtensions
{
    public static TBuilder AddEpubParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        // HtmlDocumentParser is a dependency of EpubDocumentParser — ensure it's registered.
        builder.Services.AddSingleton<HtmlDocumentParser>();
        builder.AddParser<EpubDocumentParser>();
        return builder;
    }
}
```

**Step 3: Run tests**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "EpubDocumentParserTests" -v q`
Expected: All 5 tests pass.

> **Note on `BuildEpub` helper:** `VersOne.Epub` ships `EpubBuilder` for creating test EPUBs in-memory. If this API doesn't exist in the version resolved, create the EPUB fixture manually as a zip containing the minimal OPF/NCX/HTML files, or load a checked-in `TestData/test.epub` fixture. Either approach is fine — the parser logic is what matters, not the fixture construction technique. Check `EpubBuilder` first as it is the simpler path.

**Step 4: Run full test suite**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q`
Expected: All tests pass (no regressions).

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.Epub/
git commit -m "feat(epub): add EpubDocumentParser and builder extension"
```

---

## Task 4: Email parser project scaffold

**Files:**
- Create: `src/Rag.NET.Parsers.Email/Rag.NET.Parsers.Email.csproj`
- Modify: `Rag.NET.slnx`
- Modify: `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`

**Step 1: Create the project file**

`src/Rag.NET.Parsers.Email/Rag.NET.Parsers.Email.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Parsers.Email</RootNamespace>
    <PackageId>Rag.NET.Parsers.Email</PackageId>
    <Description>EML email parser for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />
    <ProjectReference Include="..\Rag.NET.Parsers.Html\Rag.NET.Parsers.Html.csproj" />
    <PackageReference Include="MimeKit" Version="4.*" />
  </ItemGroup>

</Project>
```

**Step 2: Add to solution**

In `Rag.NET.slnx`, inside `<Folder Name="/src/">`, add after the Epub entry:
```xml
    <Project Path="src/Rag.NET.Parsers.Email/Rag.NET.Parsers.Email.csproj" />
```

**Step 3: Add project reference to tests**

In `tests/Rag.NET.Tests/Rag.NET.Tests.csproj`, add inside `<ItemGroup>`:
```xml
    <ProjectReference Include="..\..\src\Rag.NET.Parsers.Email\Rag.NET.Parsers.Email.csproj" />
```

**Step 4: Verify it builds**

Run: `dotnet build src/Rag.NET.Parsers.Email/Rag.NET.Parsers.Email.csproj`
Expected: Build succeeded (0 errors)

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.Email/ Rag.NET.slnx tests/Rag.NET.Tests/Rag.NET.Tests.csproj
git commit -m "chore: scaffold Rag.NET.Parsers.Email project"
```

---

## Task 5: Email parser — tests first

**Files:**
- Create: `tests/Rag.NET.Tests/Parsers/EmailDocumentParserTests.cs`

**Step 1: Write failing tests**

`tests/Rag.NET.Tests/Parsers/EmailDocumentParserTests.cs`:
```csharp
using System.Text;
using MimeKit;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Email;
using Rag.NET.Parsers.Html;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class EmailDocumentParserTests
{
    private readonly HtmlDocumentParser _htmlParser = new();

    private EmailDocumentParser CreateSut(params IDocumentParser[] extraParsers) =>
        new([_htmlParser, ..extraParsers], _htmlParser);

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("email-1"),
        FileName = "test.eml",
        ContentType = "message/rfc822",
    };

    [Fact]
    public void CanParse_MessageRfc822_ReturnsTrue()
    {
        Assert.True(CreateSut().CanParse("message/rfc822"));
    }

    [Fact]
    public void CanParse_OtherType_ReturnsFalse()
    {
        Assert.False(CreateSut().CanParse("text/plain"));
    }

    [Fact]
    public async Task ParseAsync_PlainTextEmail_YieldsSubjectAndBody()
    {
        using var stream = BuildEml("Hello World", textBody: "This is the body.");

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        // First section: subject
        Assert.Equal("Hello World", sections[0].Text);
        Assert.Equal(1, sections[0].HeadingLevel);
        // Second section: body
        Assert.Contains("This is the body.", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_HtmlBodyNoPlainText_ParsesHtmlBody()
    {
        var html = "<html><body><h1>Title</h1><p>Body text.</p></body></html>";
        using var stream = BuildEml("Subject", htmlBody: html);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Subject + at least one HTML section
        Assert.True(sections.Count >= 2);
        Assert.Equal("Subject", sections[0].Text);
        Assert.Contains(sections.Skip(1), s => s.Text.Contains("Title", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_WithTextAttachment_IncludesAttachmentSections()
    {
        var attachmentParser = Substitute.For<IDocumentParser>();
        attachmentParser.CanParse("text/plain").Returns(true);
        attachmentParser.ParseAsync(
                Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(YieldSection("attachment content", new DocumentId("email-1")));

        using var stream = BuildEml("Subject", textBody: "Body.", attachmentName: "notes.txt", attachmentMime: "text/plain", attachmentContent: "attachment content");

        var sections = await CreateSut(attachmentParser)
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(sections, s => s.Text.Contains("attachment content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_AttachmentWithNoParser_SkipsAttachment()
    {
        // No parser registered for application/pdf
        using var stream = BuildEml("Subject", textBody: "Body.", attachmentName: "file.pdf", attachmentMime: "application/pdf", attachmentContent: "PDF bytes");

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Subject + body only; no crash
        Assert.Equal(2, sections.Count);
    }

    [Fact]
    public async Task ParseAsync_EmptyBody_YieldsSubjectOnly()
    {
        using var stream = BuildEml("Only Subject", textBody: null);

        var sections = await CreateSut()
            .ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Equal("Only Subject", sections[0].Text);
    }

    // Helpers

    private static MemoryStream BuildEml(
        string subject,
        string? textBody = null,
        string? htmlBody = null,
        string? attachmentName = null,
        string? attachmentMime = null,
        string? attachmentContent = null)
    {
        var message = new MimeMessage();
        message.Subject = subject;
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));

        var multipart = new Multipart("mixed");

        if (textBody is not null || htmlBody is not null)
        {
            var alternative = new MultipartAlternative();
            if (textBody is not null)
                alternative.Add(new TextPart("plain") { Text = textBody });
            if (htmlBody is not null)
                alternative.Add(new TextPart("html") { Text = htmlBody });
            multipart.Add(alternative.Count == 1 ? (MimeEntity)alternative[0] : alternative);
        }

        if (attachmentName is not null && attachmentContent is not null)
        {
            var contentTypeParts = (attachmentMime ?? "application/octet-stream").Split('/');
            var part = new MimePart(contentTypeParts[0], contentTypeParts.ElementAtOrDefault(1) ?? "octet-stream")
            {
                FileName = attachmentName,
                Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(attachmentContent))),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            };
            multipart.Add(part);
        }

        message.Body = multipart.Count == 1 ? multipart[0] : multipart;

        var ms = new MemoryStream();
        message.WriteTo(ms);
        ms.Position = 0;
        return ms;
    }

    private static async IAsyncEnumerable<DocumentSection> YieldSection(string text, DocumentId id)
    {
        await Task.Yield();
        yield return new DocumentSection { Text = text, DocumentId = id, SectionIndex = 0 };
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "EmailDocumentParserTests" -v q`
Expected: Compilation error — `EmailDocumentParser` does not exist yet.

**Step 3: Commit tests**

```bash
git add tests/Rag.NET.Tests/Parsers/EmailDocumentParserTests.cs
git commit -m "test(email): add failing tests for EmailDocumentParser"
```

---

## Task 6: Email parser — implementation

**Files:**
- Create: `src/Rag.NET.Parsers.Email/EmailDocumentParser.cs`
- Create: `src/Rag.NET.Parsers.Email/EmailParserBuilderExtensions.cs`

**Step 1: Write the parser**

`src/Rag.NET.Parsers.Email/EmailDocumentParser.cs`:
```csharp
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using MimeKit;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

public sealed class EmailDocumentParser(
    IEnumerable<IDocumentParser> parsers,
    HtmlDocumentParser htmlParser,
    ILogger<EmailDocumentParser>? logger = null) : IDocumentParser
{
    public bool CanParse(string contentType) =>
        contentType.Equals("message/rfc822", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        int sectionIndex = 0;

        // Subject section
        if (!string.IsNullOrWhiteSpace(message.Subject))
        {
            yield return new DocumentSection
            {
                Text = message.Subject,
                DocumentId = metadata.DocumentId,
                Heading = message.Subject,
                HeadingLevel = 1,
                SectionIndex = sectionIndex++,
            };
        }

        // Body sections
        await foreach (var section in ParseBodyAsync(message, metadata, cancellationToken).ConfigureAwait(false))
        {
            yield return section with { SectionIndex = sectionIndex++ };
        }

        // Attachment sections
        foreach (var attachment in message.Attachments.OfType<MimePart>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(attachment.FileName))
                continue;

            var mimeType = $"{attachment.ContentType.MediaType}/{attachment.ContentType.MediaSubtype}";
            var parser = parsers.FirstOrDefault(p => p.CanParse(mimeType));

            if (parser is null)
            {
                logger?.LogWarning("No parser registered for attachment content type {ContentType}; skipping {FileName}",
                    mimeType, attachment.FileName);
                continue;
            }

            var attachmentMetadata = metadata with { FileName = attachment.FileName };
            using var attachmentStream = new MemoryStream();
            await attachment.Content.DecodeToAsync(attachmentStream, cancellationToken).ConfigureAwait(false);
            attachmentStream.Position = 0;

            await foreach (var section in parser.ParseAsync(attachmentStream, attachmentMetadata, cancellationToken).ConfigureAwait(false))
            {
                yield return section with { SectionIndex = sectionIndex++ };
            }
        }
    }

    private async IAsyncEnumerable<DocumentSection> ParseBodyAsync(
        MimeMessage message,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Prefer plain text
        if (!string.IsNullOrWhiteSpace(message.TextBody))
        {
            yield return new DocumentSection
            {
                Text = message.TextBody.Trim(),
                DocumentId = metadata.DocumentId,
                SectionIndex = 0,
            };
            yield break;
        }

        // Fall back to HTML body
        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            using var htmlStream = new MemoryStream(Encoding.UTF8.GetBytes(message.HtmlBody));
            await foreach (var section in htmlParser.ParseAsync(htmlStream, metadata, cancellationToken).ConfigureAwait(false))
            {
                yield return section;
            }
        }
    }
}
```

**Step 2: Write the builder extension**

`src/Rag.NET.Parsers.Email/EmailParserBuilderExtensions.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

public static class EmailParserBuilderExtensions
{
    public static TBuilder AddEmailParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<HtmlDocumentParser>();
        builder.AddParser<EmailDocumentParser>();
        return builder;
    }
}
```

**Step 3: Run email tests**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "EmailDocumentParserTests" -v q`
Expected: All 6 tests pass.

**Step 4: Run full test suite**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q`
Expected: All tests pass (no regressions).

**Step 5: Commit**

```bash
git add src/Rag.NET.Parsers.Email/
git commit -m "feat(email): add EmailDocumentParser with attachment dispatch"
```

---

## Task 7: Update features backlog

**Files:**
- Modify: `docs/reference/features.md`

**Step 1: Mark EPUB parser as done**

Find the `### EPUB Parser` section (currently has no Status line). Add above the `---` closing line:
```markdown
**Status:** ✅ Done
```

**Step 2: Mark Email File Parser as done**

Find the `### Email File Parser (EML / MSG)` section. Add:
```markdown
**Status:** ✅ Done (EML only; MSG is a follow-up)
```

**Step 3: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark EPUB and EML parsers as done in feature backlog"
```

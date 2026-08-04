# Rag.NET.Parsers.Office

Office document parsers for the Rag.NET ingestion pipeline: Word (`.docx`), Excel
(`.xlsx`) and PowerPoint (`.pptx`), read with DocumentFormat.OpenXml — no Office
installation required.

## Install

```bash
dotnet add package Rag.NET.Parsers.Office
```

Install alongside the core pipeline package (`dotnet add package Rag.NET`), which supplies
the `AddRagNet(...)` builder the parsers register into.

## Setup

Each format has its own registration — add only what you ingest. Inside your
`AddRagNet(...)` builder callback:

```csharp
using Rag.NET.Parsers.Excel;
using Rag.NET.Parsers.PowerPoint;
using Rag.NET.Parsers.Word;

rag.AddWordParser()
   .AddExcelParser()
   .AddPowerPointParser();
```

## Example

With the parsers registered, Office files flow through the same ingest call as everything
else — the pipeline picks the parser by content type:

```csharp
using var stream = File.OpenRead("hr-policy.docx");
var result = await pipeline.IngestAsync(stream, new DocumentMetadata
{
    DocumentId  = new DocumentId("policy-hr-001"),
    FileName    = "hr-policy.docx",
    ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
});
```

Word paragraphs and headings become structured text, Excel sheets become row-wise text
per sheet, and PowerPoint slides are emitted slide-by-slide with their notes.

## Full guide

- [Ingestion and parsers](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/guide/ingestion.md)

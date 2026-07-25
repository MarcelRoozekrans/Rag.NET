namespace Rag.NET.Parsers.Pdf.TableExtraction;

/// <summary>
/// A single word on a page in top-down page coordinates: <paramref name="X"/> is the left edge,
/// <paramref name="Y"/> the top edge measured from the top of the page (Y grows downward —
/// PdfPig's bottom-up coordinates are normalized before construction).
/// </summary>
internal readonly record struct WordBox(string Text, double X, double Y, double Width, double Height);

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
public sealed partial class CSharpChunkingStrategy : IChunkingStrategy
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

        // If there are parse errors, fall back to a single chunk with the raw text
        if (root.ContainsDiagnostics && root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            LogParseError(_logger, section.DocumentId);
            yield return new TextChunk
            {
                Text = section.Text,
                DocumentId = section.DocumentId,
                ChunkIndex = 0,
                Metadata = PageMetadata.ForPage(section.PageNumber),
            };
            yield break;
        }

        // Full member extraction
        await foreach (var chunk in ExtractMembersAsync(root, section, options, cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }

    [LoggerMessage(EventId = 879715426, EventName = "log_parse_error", Level = LogLevel.Warning, Message = "C# parse errors in document {DocumentId}; falling back to single chunk")]
    private static partial void LogParseError(ILogger logger, DocumentId documentId);

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

            var chunkMetadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
            foreach (var kv in metadata)
                chunkMetadata[kv.Key] = kv.Value;
            PageMetadata.Write(chunkMetadata, section.PageNumber, section.PageNumber);

            if (text.Length > options.MaxChunkSize)
                chunkMetadata["csharp.oversized"] = "true";

            yield return new TextChunk
            {
                Text = text,
                DocumentId = section.DocumentId,
                ChunkIndex = chunkIndex++,
                Metadata = chunkMetadata,
                StartPosition = node.FullSpan.Start,
                EndPosition = node.FullSpan.End,
            };
        }
    }

    private static string ExtractSignatureAndDoc(SyntaxNode node)
    {
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
        if (body1 is not null)
        {
            var idx = text.IndexOf(body1.ToFullString(), StringComparison.Ordinal);
            if (idx >= 0)
                text = text[..idx].TrimEnd() + ";";
        }
        if (body2 is not null)
        {
            var idx = text.IndexOf(body2.ToFullString(), StringComparison.Ordinal);
            if (idx >= 0)
                text = text[..idx].TrimEnd() + ";";
        }
        return text.Trim();
    }

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
            if (string.Equals(accessibility, "private", StringComparison.Ordinal) && !_options.IncludePrivateMembers) return;
            if (string.Equals(accessibility, "internal", StringComparison.Ordinal) && !_options.IncludeInternalMembers) return;

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
            bool isPublic    = false;
            bool isProtected = false;
            bool isInternal  = false;
            bool isPrivate   = false;

            var modifiers = node.Modifiers;
            for (int i = 0; i < modifiers.Count; i++)
            {
                var rawKind = modifiers[i].RawKind;
                if (rawKind == (int)SyntaxKind.PublicKeyword)    isPublic    = true;
                if (rawKind == (int)SyntaxKind.ProtectedKeyword) isProtected = true;
                if (rawKind == (int)SyntaxKind.InternalKeyword)  isInternal  = true;
                if (rawKind == (int)SyntaxKind.PrivateKeyword)   isPrivate   = true;
            }

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
            SyntaxTrivia trivia = default;
            bool found = false;
            var leadingTrivia = node.GetLeadingTrivia();
            for (int i = 0; i < leadingTrivia.Count; i++)
            {
                var rawKind = leadingTrivia[i].RawKind;
                if (rawKind == (int)SyntaxKind.SingleLineDocumentationCommentTrivia
                    || rawKind == (int)SyntaxKind.MultiLineDocumentationCommentTrivia)
                {
                    trivia = leadingTrivia[i];
                    found = true;
                    break;
                }
            }

            if (!found)
                return string.Empty;

            var xml = trivia.ToString();

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
}

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

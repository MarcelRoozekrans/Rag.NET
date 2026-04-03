namespace Rag.NET.Security;

public sealed class TrustLevelGuardOptions
{
    /// <summary>When true, chunks with trust_level=untrusted are removed from results. Default: true.</summary>
    public bool DropUntrusted { get; set; } = true;

    /// <summary>When true, chunks with trust_level=external emit a Warning log. Default: true.</summary>
    public bool WarnOnExternal { get; set; } = true;
}

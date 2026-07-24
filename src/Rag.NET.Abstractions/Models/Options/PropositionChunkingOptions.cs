using Microsoft.Extensions.AI;

namespace Rag.NET.Models.Options;

/// <summary>Options for proposition-extraction chunking.</summary>
public sealed class PropositionChunkingOptions
{
    /// <summary>Max tokens (cl100k_base) per passage sent to the LLM.</summary>
    public int MaxPassageTokens { get; set; } = 1000;

    /// <summary>Safety cap on propositions parsed per passage.</summary>
    public int MaxPropositionsPerPassage { get; set; } = 50;

    /// <summary>Also emit each source passage as its own chunk (for dual-index setups).</summary>
    public bool EmitParentPassages { get; set; }

    /// <summary>Optional dedicated chat client; falls back to the DI-registered one.</summary>
    public IChatClient? ChatClient { get; set; }
}

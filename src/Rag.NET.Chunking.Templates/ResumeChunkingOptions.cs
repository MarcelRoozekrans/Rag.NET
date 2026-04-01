using Microsoft.Extensions.AI;

namespace Rag.NET.Chunking.Templates;

public sealed class ResumeChunkingOptions
{
    /// <summary>Optional model override. Null = use constructor-injected IChatClient.</summary>
    public IChatClient? ChatClient { get; set; }

    public string Prompt { get; set; } = """
        Extract the following sections from this resume as JSON. Return ONLY valid JSON, no markdown.

        {
          "contact_info": "full contact block as a single string",
          "work_history": [{"company": "...", "title": "...", "dates": "...", "description": "..."}],
          "education": [{"institution": "...", "degree": "...", "dates": "..."}],
          "skills": "skills as a single string"
        }

        Resume:
        {text}
        """;
}

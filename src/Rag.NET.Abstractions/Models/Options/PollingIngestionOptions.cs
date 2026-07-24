namespace Rag.NET.Models.Options;

/// <summary>Options for one background polling trigger registration.</summary>
public sealed class PollingIngestionOptions
{
    /// <summary>
    /// Delay between polling cycles. Must be greater than zero. Default 5 minutes.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Required stable identifier for the polled source. Scopes content-hash bookkeeping
    /// so unchanged files are skipped across cycles, and appears in cycle log messages.
    /// </summary>
    public string ProviderId { get; set; } = string.Empty;
}

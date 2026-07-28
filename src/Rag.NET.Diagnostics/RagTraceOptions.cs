using Rag.NET.Security;

namespace Rag.NET.Diagnostics;

/// <summary>Controls how much a trace keeps, and whether it keeps any text at all.</summary>
/// <remarks>
/// <para>
/// <b>Every <c>Capture*</c> flag defaults to <see langword="false"/>.</b> Registering diagnostics
/// captures <i>structure</i> — chunk ids, scores, stage latencies, which guards fired and how many
/// chunks each removed. Capturing the <i>text</i> takes a further explicit flag per field, because
/// <i>"turn on debugging"</i> must never silently mean <i>"start retaining customer documents and
/// user questions in process memory"</i>.
/// </para>
/// <para>
/// This is the same posture <see cref="AuditLogOptions"/> takes with
/// <see cref="AuditLogOptions.LogQueryText"/> and <see cref="AuditLogOptions.LogAnswerText"/>. The
/// prefixes differ — <c>Capture*</c> here, <c>Log*</c> there — because these two subsystems keep
/// content for different lengths of time in different places; the rule they express is one concept,
/// not two.
/// </para>
/// <para>
/// Memory is bounded by <see cref="Capacity"/> <i>and</i> <see cref="MaxCapturedCharacters"/>
/// together. Without the second, a large capacity quietly means tens of megabytes of document text.
/// The worst case is <c>Capacity × (TopK + 1) × MaxCapturedCharacters</c> characters.
/// </para>
/// </remarks>
public sealed class RagTraceOptions
{
    /// <summary>How many recent executions to keep. Default 50, minimum 1.</summary>
    /// <remarks>
    /// Older traces are evicted once the buffer is full, so this is a hard ceiling rather than a
    /// target. Raise it and the worst-case memory in the type remarks rises with it, linearly.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is below 1. Validated here rather than only where the buffer is built, so the
    /// exception names the property that was set. It cannot name it through <c>paramName</c>: an
    /// <c>init</c> accessor's only parameter is <c>value</c>, so the property name goes in the
    /// message instead.
    /// </exception>
    public int Capacity
    {
        get => _capacity;
        init
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"{nameof(Capacity)} must be at least 1. A buffer that keeps nothing is not a " +
                    "smaller debugger, it is a disabled one — leave diagnostics unregistered instead.");
            }

            _capacity = value;
        }
    }

    private readonly int _capacity = 50;

    /// <summary>
    /// When <see langword="true"/>, <see cref="RagTrace.Query"/> holds the question the user typed.
    /// </summary>
    /// <remarks>
    /// <b>What this puts in memory:</b> the raw text of every traced user question, retained until
    /// evicted, readable by anything in the process. <see cref="RagTrace.QueryHash"/> is populated
    /// either way, so leave this off if you only need to tell repeated questions apart. The
    /// compliance-grade equivalent is <see cref="AuditLogOptions.LogQueryText"/>, which writes the
    /// same text somewhere durable instead.
    /// </remarks>
    public bool CaptureQueryText { get; init; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="RagTrace.ChunkTexts"/> holds the retrieved chunks'
    /// contents, and guard actions record the text they were given and returned.
    /// </summary>
    /// <remarks>
    /// <b>What this puts in memory:</b> the body text of every retrieved chunk — that is, your
    /// indexed documents — for every traced query. This is the largest of the four by far, and the
    /// one most likely to hold something confidential. It is also the flag that makes <i>"what did
    /// the sanitiser remove"</i> answerable, and that text is captured <b>before</b> redaction, so
    /// a trace may hold content the pipeline itself went on to strip.
    /// </remarks>
    public bool CaptureChunkText { get; init; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="RagTrace.Prompt"/> holds the prompt the answer engine
    /// was given.
    /// </summary>
    /// <remarks>
    /// <b>What this puts in memory:</b> the assembled prompt, which contains the question and the
    /// retrieved chunks together — so this retains the same content as
    /// <see cref="CaptureQueryText"/> and <see cref="CaptureChunkText"/> combined, in one field, and
    /// counts against <see cref="MaxCapturedCharacters"/> once rather than per chunk.
    /// </remarks>
    public bool CapturePromptText { get; init; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="RagTrace.Answer"/> holds the generated answer.
    /// </summary>
    /// <remarks>
    /// <b>What this puts in memory:</b> the model's reply to every traced query, which is derived
    /// from your documents and can quote them at length. The compliance-grade equivalent is
    /// <see cref="AuditLogOptions.LogAnswerText"/>.
    /// </remarks>
    public bool CaptureAnswerText { get; init; }

    /// <summary>
    /// The longest any single captured text field may be, in characters. Default 4000; 0 keeps none.
    /// </summary>
    /// <remarks>
    /// Applied per field, not per trace, and truncation is made visible rather than silent — a trace
    /// that looks complete but is not would mislead exactly when someone is debugging. Setting it to
    /// 0 leaves the <c>Capture*</c> flags on but captures no characters, which is a way to confirm
    /// the wiring without retaining anything.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative. As with <see cref="Capacity"/>, the property is named in the message
    /// because an <c>init</c> accessor's only parameter is <c>value</c>.
    /// </exception>
    public int MaxCapturedCharacters
    {
        get => _maxCapturedCharacters;
        init
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    $"{nameof(MaxCapturedCharacters)} must not be negative. To capture no text at " +
                    $"all, leave the {nameof(CaptureQueryText)}-style flags off; 0 is the way to " +
                    "keep them on and still retain nothing.");
            }

            _maxCapturedCharacters = value;
        }
    }

    private readonly int _maxCapturedCharacters = 4000;
}

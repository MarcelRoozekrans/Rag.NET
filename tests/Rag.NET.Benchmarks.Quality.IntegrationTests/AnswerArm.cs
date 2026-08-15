namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>The three arms by name — the strings the environment variable and the pin table use.</summary>
internal static class AnswerArm
{
    public const string Dense = "dense";
    public const string Local = "local";
    public const string Global = "global";

    public static readonly IReadOnlyList<string> All = [Dense, Local, Global];
}

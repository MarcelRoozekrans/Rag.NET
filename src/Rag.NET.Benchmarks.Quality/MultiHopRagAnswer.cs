namespace Rag.NET.Benchmarks.Quality;

/// <summary>One query's gold answer and the type MultiHop-RAG files the query under.</summary>
/// <param name="QueryId">The query id <c>queries.jsonl</c> uses.</param>
/// <param name="Answer">
/// The published answer — an entity for inference queries, <c>yes</c>/<c>no</c> for comparison,
/// <c>yes</c>/<c>no</c>/<c>before</c>/<c>after</c> for temporal, and "Insufficient information."
/// for null queries.
/// </param>
/// <param name="QuestionType">One of the four <c>question_type</c> values.</param>
public sealed record MultiHopRagAnswer(string QueryId, string Answer, string QuestionType);

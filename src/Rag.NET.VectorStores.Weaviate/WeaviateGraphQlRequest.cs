using System.Text.Json.Serialization;

namespace Rag.NET.Weaviate;

/// <summary>
/// GraphQL request envelope: the query document only. Unlike the Linear conventions,
/// no <c>variables</c> field is sent — Weaviate's GraphQL API types its arguments as
/// per-class custom scalars (e.g. <c>GetObjects{Class}NearVectorVectorScalar</c>) that
/// reject values supplied through GraphQL variables (verified empirically against
/// Weaviate 1.39: both <c>[Float]</c>-typed and scalar-typed variables error), so the
/// store inlines every argument into the query document with proper escaping.
/// </summary>
public sealed record WeaviateGraphQlRequest(
    [property: JsonPropertyName("query")] string Query);

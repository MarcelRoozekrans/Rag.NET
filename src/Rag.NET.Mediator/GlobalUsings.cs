// Workaround: ZeroAlloc.Mediator.Generator emits RagError unqualified in generated dispatch code,
// causing CS0246 when RagError is not in scope. This alias ensures it resolves correctly.
global using RagError = Rag.NET.Models.RagError;

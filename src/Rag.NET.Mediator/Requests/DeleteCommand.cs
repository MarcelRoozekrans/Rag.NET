using Rag.NET.Models;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Rag.NET.Mediator.Requests;

public sealed record DeleteCommand(DocumentId DocumentId)
    : IRequest<Result<Unit, RagError>>;

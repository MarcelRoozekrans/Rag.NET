using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Mediator.Handlers;
using ZeroAlloc.Mediator;

namespace Rag.NET.Mediator.DependencyInjection;

public static class MediatorBuilderExtensions
{
    public static RagBuilder UseMediator(this RagBuilder builder)
    {
        builder.Services.AddTransient<IngestCommandHandler>();
        builder.Services.AddTransient<RetrieveQueryHandler>();
        builder.Services.AddTransient<DeleteCommandHandler>();

        builder.Services.AddSingleton<IMediator>(sp =>
        {
            ZeroAlloc.Mediator.Mediator.Configure(cfg =>
            {
                cfg.SetFactory<IngestCommandHandler>(() => sp.GetRequiredService<IngestCommandHandler>());
                cfg.SetFactory<RetrieveQueryHandler>(() => sp.GetRequiredService<RetrieveQueryHandler>());
                cfg.SetFactory<DeleteCommandHandler>(() => sp.GetRequiredService<DeleteCommandHandler>());
            });
            return new MediatorService();
        });

        return builder;
    }
}

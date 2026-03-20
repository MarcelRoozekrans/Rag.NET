using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Mediator.Handlers;
using ZeroAlloc.Mediator;

namespace Rag.NET.Mediator.DependencyInjection;

public static class MediatorServiceCollectionExtensions
{
    public static IServiceCollection AddRagNETMediator(this IServiceCollection services)
    {
        services.AddTransient<IngestCommandHandler>();
        services.AddTransient<RetrieveQueryHandler>();
        services.AddTransient<DeleteCommandHandler>();

        services.AddSingleton<IMediator>(sp =>
        {
            ZeroAlloc.Mediator.Mediator.Configure(cfg =>
            {
                cfg.SetFactory<IngestCommandHandler>(() => sp.GetRequiredService<IngestCommandHandler>());
                cfg.SetFactory<RetrieveQueryHandler>(() => sp.GetRequiredService<RetrieveQueryHandler>());
                cfg.SetFactory<DeleteCommandHandler>(() => sp.GetRequiredService<DeleteCommandHandler>());
            });
            return new MediatorService();
        });

        return services;
    }
}

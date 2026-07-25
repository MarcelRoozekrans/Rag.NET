using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class ServiceDecorationHelperTests
{
    public interface IWidget
    {
        string Name { get; }
    }

    public sealed class Widget(string name) : IWidget
    {
        public string Name => name;
    }

    public sealed class WidgetDependency
    {
        public string Value => "dep";
    }

    public sealed class DependentWidget(WidgetDependency dependency) : IWidget
    {
        public string Name => "typed:" + dependency.Value;
    }

    public sealed class WidgetDecorator(IWidget inner) : IWidget
    {
        public IWidget Inner { get; } = inner;

        public string Name => "decorated:" + Inner.Name;
    }

    private static void DecorateWidget(IServiceCollection services) =>
        ServiceDecorationHelper.Decorate<IWidget>(services, (inner, _) => new WidgetDecorator(inner));

    [Fact]
    public void Decorate_InstanceRegistration_WrapsTheOriginalInstance()
    {
        var original = new Widget("instance");
        var services = new ServiceCollection();
        services.AddSingleton<IWidget>(original);

        DecorateWidget(services);
        var resolved = services.BuildServiceProvider().GetRequiredService<IWidget>();

        var decorator = Assert.IsType<WidgetDecorator>(resolved);
        Assert.Same(original, decorator.Inner);
    }

    [Fact]
    public void Decorate_FactoryRegistration_WrapsTheFactoryResult()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWidget>(_ => new Widget("factory"));

        DecorateWidget(services);
        var resolved = services.BuildServiceProvider().GetRequiredService<IWidget>();

        var decorator = Assert.IsType<WidgetDecorator>(resolved);
        Assert.Equal("decorated:factory", decorator.Name);
    }

    [Fact]
    public void Decorate_TypeRegistration_ActivatesWithDependenciesFromContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<WidgetDependency>();
        services.AddSingleton<IWidget, DependentWidget>();

        DecorateWidget(services);
        var resolved = services.BuildServiceProvider().GetRequiredService<IWidget>();

        var decorator = Assert.IsType<WidgetDecorator>(resolved);
        Assert.IsType<DependentWidget>(decorator.Inner);
        Assert.Equal("decorated:typed:dep", decorator.Name);
    }

    [Fact]
    public void Decorate_NoRegistration_ThrowsActionable()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => DecorateWidget(services));

        Assert.Contains("IWidget", ex.Message, StringComparison.Ordinal);
        Assert.Contains("before", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decorate_MultipleRegistrations_WrapsTheLastOne()
    {
        var first = new Widget("first");
        var last = new Widget("last");
        var services = new ServiceCollection();
        services.AddSingleton<IWidget>(first);
        services.AddSingleton<IWidget>(last);

        DecorateWidget(services);
        var provider = services.BuildServiceProvider();

        var decorator = Assert.IsType<WidgetDecorator>(provider.GetRequiredService<IWidget>());
        Assert.Same(last, decorator.Inner);

        // The earlier registration is untouched and still resolvable via IEnumerable<T>.
        var all = provider.GetServices<IWidget>().ToList();
        Assert.Equal(2, all.Count);
        Assert.Same(first, all[0]);
    }

    [Fact]
    public void Decorate_ReRegistersAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWidget>(_ => new Widget("x"));

        DecorateWidget(services);
        var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IWidget>(), provider.GetRequiredService<IWidget>());
    }
}

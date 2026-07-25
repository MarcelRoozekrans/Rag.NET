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

    public sealed class DisposableWidget : IWidget, IDisposable
    {
        public string Name => "disposable";

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
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
    public void Decorate_SingletonRegistration_StaysSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWidget>(_ => new Widget("x"));

        DecorateWidget(services);
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IWidget>(), provider.GetRequiredService<IWidget>());
    }

    [Fact]
    public void Decorate_ScopedRegistration_StaysScoped()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWidget>(_ => new Widget("scoped"));

        DecorateWidget(services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var fromScope1A = Assert.IsType<WidgetDecorator>(scope1.ServiceProvider.GetRequiredService<IWidget>());
        var fromScope1B = Assert.IsType<WidgetDecorator>(scope1.ServiceProvider.GetRequiredService<IWidget>());
        var fromScope2 = Assert.IsType<WidgetDecorator>(scope2.ServiceProvider.GetRequiredService<IWidget>());

        Assert.Same(fromScope1A, fromScope1B);                 // cached within a scope
        Assert.NotSame(fromScope1A, fromScope2);               // distinct decorator per scope
        Assert.NotSame(fromScope1A.Inner, fromScope2.Inner);   // distinct inner per scope
    }

    [Fact]
    public void Decorate_FactoryCreatedInner_IsDisposedWithTheProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWidget>(_ => new DisposableWidget());

        DecorateWidget(services);
        var provider = services.BuildServiceProvider();
        var decorator = Assert.IsType<WidgetDecorator>(provider.GetRequiredService<IWidget>());
        var inner = Assert.IsType<DisposableWidget>(decorator.Inner);

        provider.Dispose();

        Assert.True(inner.Disposed); // the container materialised it, so the container disposes it
    }

    [Fact]
    public void Decorate_InstanceRegisteredInner_IsNotDisposedWithTheProvider()
    {
        var instance = new DisposableWidget();
        var services = new ServiceCollection();
        services.AddSingleton<IWidget>(instance);

        DecorateWidget(services);
        var provider = services.BuildServiceProvider();
        Assert.IsType<WidgetDecorator>(provider.GetRequiredService<IWidget>());

        provider.Dispose();

        Assert.False(instance.Disposed); // the container never owned it — external ownership preserved
    }

    [Fact]
    public void Decorate_KeyedRegistrationsOfTheServiceType_AreIgnoredAndLeftUndecorated()
    {
        var keyed = new Widget("keyed");
        var nonKeyed = new Widget("plain");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IWidget>("my-key", keyed);
        services.AddSingleton<IWidget>(nonKeyed);

        DecorateWidget(services);
        using var provider = services.BuildServiceProvider();

        var decorator = Assert.IsType<WidgetDecorator>(provider.GetRequiredService<IWidget>());
        Assert.Same(nonKeyed, decorator.Inner);
        Assert.Same(keyed, provider.GetRequiredKeyedService<IWidget>("my-key")); // untouched
    }

    [Fact]
    public void Decorate_CalledTwice_StacksDecoratorsWithoutRecursion()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWidget>(_ => new Widget("core"));

        DecorateWidget(services);
        DecorateWidget(services);
        using var provider = services.BuildServiceProvider();

        var outer = Assert.IsType<WidgetDecorator>(provider.GetRequiredService<IWidget>());
        var middle = Assert.IsType<WidgetDecorator>(outer.Inner);
        Assert.Equal("core", Assert.IsType<Widget>(middle.Inner).Name);
    }
}

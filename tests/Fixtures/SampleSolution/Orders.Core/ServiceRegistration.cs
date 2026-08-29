namespace Orders;

/// <summary>
/// Fixture for L2's di.json: a DI-container-shaped registration site. `FakeServiceCollection` is a stand-in for
/// Microsoft.Extensions.DependencyInjection's IServiceCollection so the fixture stays offline/self-contained —
/// the walker's DI detection only pattern-matches the syntactic method name (AddScoped/AddSingleton/AddTransient)
/// and resolves the generic type arguments, it doesn't require this to be the real extension method.
/// </summary>
public class FakeServiceCollection
{
    public void AddScoped<TService, TImplementation>() where TImplementation : TService
    {
    }
}

public static class ServiceRegistration
{
    public static void Configure(FakeServiceCollection services)
    {
        services.AddScoped<IOrderService, OrderService>();

        // Deliberately disagrees with EmailNotifier's [Injectable] binding (INotifier) — fixture for the
        // attribute-vs-fluent conflict diagnostic (P10). NotifierBase is a base CLASS, so it doesn't affect
        // EmailNotifier's "exactly 1 real interface" count (only INotifier is a real interface it implements).
        services.AddScoped<NotifierBase, EmailNotifier>();
    }
}

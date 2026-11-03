namespace Conduit.Tests.Support;

/// <summary>
/// An <see cref="IServiceProvider"/> over a fixed set of services that counts every <c>GetService</c> call and can
/// throw for one service type, for proving a component looks nothing up, or survives a lookup that fails.
/// </summary>
public sealed class CountingServiceProvider(
    IReadOnlyDictionary<Type, object> services, Type? throwsFor = null) : IServiceProvider
{
    public int Calls { get; private set; }

    public object? GetService(Type serviceType)
    {
        this.Calls++;
        if (serviceType == throwsFor)
        {
            throw new InvalidOperationException("cannot build " + serviceType.Name);
        }

        return services.GetValueOrDefault(serviceType);
    }
}

using System.Reflection;
using Conduit.Jobs.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// An <see cref="IJobStore"/> that forwards every member to a real store, running its <see cref="JobStoreHook"/>
/// first when the hook is armed for the member called.
/// </summary>
public class HookedJobStore : DispatchProxy
{
    private IJobStore inner = null!;
    private JobStoreHook hook = null!;

    /// <summary>
    /// Replaces the registered <see cref="IJobStore"/> with one that wraps it and fires <paramref name="hook"/>.
    /// Call after <c>AddJobs</c>, which registers the store being replaced.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="hook">The hook every wrapped store shares.</param>
    public static void Install(IServiceCollection services, JobStoreHook hook)
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = services.Single(d => d.ServiceType == typeof(IJobStore));
        services.Remove(descriptor);
        services.AddScoped(provider =>
        {
            var proxy = Create<IJobStore, HookedJobStore>();
            var hooked = (HookedJobStore)(object)proxy;
            hooked.inner = (IJobStore)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
            hooked.hook = hook;
            return proxy;
        });
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        this.hook.RunIfArmed(targetMethod.Name);
        try
        {
            return targetMethod.Invoke(this.inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }
}

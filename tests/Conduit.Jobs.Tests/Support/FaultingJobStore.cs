using System.Reflection;
using Conduit.Jobs.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// An <see cref="IJobStore"/> that forwards every member to a real store, except that a call its
/// <see cref="JobStoreFault"/> has armed throws, as a database error (a deadlock victim, say) would.
/// </summary>
public class FaultingJobStore : DispatchProxy
{
    private IJobStore inner = null!;
    private JobStoreFault fault = null!;

    /// <summary>
    /// Replaces the registered <see cref="IJobStore"/> with one that wraps it and raises
    /// <paramref name="fault"/>. Call after <c>AddJobs</c>, which registers the store being replaced.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="fault">The fault every wrapped store shares.</param>
    public static void Install(IServiceCollection services, JobStoreFault fault)
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = services.Single(d => d.ServiceType == typeof(IJobStore));
        services.Remove(descriptor);
        services.AddScoped(provider => Wrap(
            (IJobStore)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!), fault));
    }

    /// <summary>Wraps a real store.</summary>
    /// <param name="inner">The store every member forwards to.</param>
    /// <param name="fault">The fault this store raises.</param>
    /// <returns>The faulting store.</returns>
    public static IJobStore Wrap(IJobStore inner, JobStoreFault fault)
    {
        var proxy = Create<IJobStore, FaultingJobStore>();
        var faulting = (FaultingJobStore)(object)proxy;
        faulting.inner = inner;
        faulting.fault = fault;
        return proxy;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (this.fault.TryTake(targetMethod.Name))
        {
            throw new InvalidOperationException("the database chose this call as a deadlock victim, on purpose");
        }

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

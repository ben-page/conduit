using System.Reflection;
using Conduit.Jobs.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// An <see cref="IJobStore"/> that forwards every member to a real store and counts the calls in a
/// <see cref="JobStoreCalls"/>, for tests that assert on how often the runner touched the ledger rather
/// than only on where the ledger ended up.
/// </summary>
public class CountingJobStore : DispatchProxy
{
    private IJobStore inner = null!;
    private JobStoreCalls calls = null!;

    /// <summary>
    /// Replaces the registered <see cref="IJobStore"/> with one that wraps it and counts into
    /// <paramref name="calls"/>. Call after <c>AddJobs</c>, which registers the store being replaced.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="calls">The counts every wrapped store shares.</param>
    public static void Install(IServiceCollection services, JobStoreCalls calls)
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = services.Single(d => d.ServiceType == typeof(IJobStore));
        services.Remove(descriptor);
        services.AddScoped(provider =>
        {
            var proxy = Create<IJobStore, CountingJobStore>();
            var counting = (CountingJobStore)(object)proxy;
            counting.inner = (IJobStore)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
            counting.calls = calls;
            return proxy;
        });
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        this.calls.Record(targetMethod.Name);
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

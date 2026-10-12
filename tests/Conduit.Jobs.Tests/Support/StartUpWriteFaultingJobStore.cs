using System.Reflection;
using Conduit.Jobs.Persistence;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// An <see cref="IJobStore"/> that forwards every member to a real store, except that the first
/// no-overlap write of a run without an occurrence (a <c>RunAtStart()</c> start-up run) throws, as a
/// database error would. A scheduled occurrence's write passes untouched.
/// </summary>
public class StartUpWriteFaultingJobStore : DispatchProxy
{
    private static int faultsRemaining;

    private IJobStore inner = null!;

    /// <summary>Arms the fault: the next start-up write on any store from this proxy throws.</summary>
    public static void ArmOneFault() => Volatile.Write(ref faultsRemaining, 1);

    /// <summary>Wraps a real store.</summary>
    /// <param name="inner">The store every member forwards to.</param>
    /// <returns>The faulting store.</returns>
    public static IJobStore Wrap(IJobStore inner)
    {
        var proxy = Create<IJobStore, StartUpWriteFaultingJobStore>();
        ((StartUpWriteFaultingJobStore)(object)proxy).inner = inner;
        return proxy;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.Name == nameof(IJobStore.TryMaterializeWithoutOverlapAsync)
            && args?[0] is JobRun { Occurrence: null }
            && Interlocked.Decrement(ref faultsRemaining) >= 0)
        {
            throw new InvalidOperationException("the database is unavailable, on purpose");
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

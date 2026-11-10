using System.Collections.Concurrent;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// How many times each <see cref="Persistence.IJobStore"/> member was called, through every store a
/// <see cref="CountingJobStore"/> wraps for one host. Held per test, so parallel tests cannot share it.
/// </summary>
public sealed class JobStoreCalls
{
    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.Ordinal);

    /// <summary>How many times <paramref name="memberName"/> was called.</summary>
    public int Count(string memberName) => this.counts.GetValueOrDefault(memberName);

    /// <summary>Records one call of <paramref name="memberName"/>.</summary>
    public void Record(string memberName) => this.counts.AddOrUpdate(memberName, 1, (_, count) => count + 1);
}

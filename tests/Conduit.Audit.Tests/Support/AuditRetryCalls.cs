namespace Conduit.Audit.Tests.Support;

/// <summary>A singleton that counts how many times each retry-test handler has run, so a handler fails on its first calls only.</summary>
public sealed class AuditRetryCalls
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, int> calls = [];

    /// <summary>Counts one more run of <paramref name="site"/> and returns the new count.</summary>
    public int Count(string site)
    {
        lock (this.gate)
        {
            this.calls[site] = this.calls.GetValueOrDefault(site) + 1;
            return this.calls[site];
        }
    }
}

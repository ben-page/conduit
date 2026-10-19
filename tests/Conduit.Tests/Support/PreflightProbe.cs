using System.Collections.Concurrent;
using Conduit.Runtime;

namespace Conduit.Tests.Support;

/// <summary>What the preflight tests' behaviors and interceptor saw: each site in the order it ran, and the context the
/// gates ran under.</summary>
public sealed class PreflightProbe
{
    private readonly ConcurrentQueue<string> sites = new();
    private readonly ConcurrentQueue<MessageContext?> gateContexts = new();

    public IReadOnlyList<string> Sites => [.. this.sites];

    public IReadOnlyList<MessageContext?> GateContexts => [.. this.gateContexts];

    public void See(string site) => this.sites.Enqueue(site);

    public void SeeGate(MessageContext? context) => this.gateContexts.Enqueue(context);
}

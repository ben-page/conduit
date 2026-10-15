using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.Durability.Runtime.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Conduit.Durability.Tests.Unit;

/// <summary>
/// A capture-only outbox writes rows on the caller's transaction and never delivers them: no relay, no
/// post-commit flush. For a host sharing an outbox table with another host whose relay owns delivery
/// (DMS's identity host, ruling 26): a second relay could claim a row whose listener lives only in the
/// other process and deliver it to nobody.
/// </summary>
[TestClass]
public sealed class CaptureOnlyOutboxShould
{
    private static ServiceCollection Configure(bool captureOnly)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options =>
            {
                options.AutoCreateTable = false;
                options.CaptureOnly = captureOnly;
            })));
        return services;
    }

    [TestMethod]
    public void RegisterNoRelay()
    {
        var services = Configure(captureOnly: true);

        services.Should().NotContain(d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxRelay));
    }

    [TestMethod]
    public void StillRegisterTheRelayByDefault()
    {
        var services = Configure(captureOnly: false);

        services.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxRelay));
    }

    [TestMethod]
    public void StillCapture_ButFlushNothing()
    {
        var services = Configure(captureOnly: true);

        services.Should().Contain(d => d.ServiceType == typeof(IOutboundInterceptor),
            "capture is the point: rows are written on the caller's transaction");
        services.Should().Contain(d =>
            d.ServiceType == typeof(IOutboxFlusher) && d.ImplementationType == typeof(CaptureOnlyOutboxFlusher));
    }

    [TestMethod]
    public void RegisterThePendingWriterInEitherMode()
    {
        // Writing is not delivery: a capture-only host's own transactional behavior still writes
        // what it captured after its pipeline's last OutboxBehavior exited.
        foreach (var captureOnly in new[] { true, false })
        {
            Configure(captureOnly).Should().Contain(d =>
                d.ServiceType == typeof(IOutboxPendingWriter)
                && d.ImplementationType == typeof(OutboxPendingWriter)
                && d.Lifetime == ServiceLifetime.Scoped);
        }
    }
}

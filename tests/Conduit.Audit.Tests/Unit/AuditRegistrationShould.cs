using AwesomeAssertions;
using Conduit.Audit.Runtime;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Audit.Tests.Unit;

[TestClass]
public sealed class AuditRegistrationShould
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RegisterTheSystemClockUnlessTheHostHasOne(bool capture)
    {
        // Arrange — one host registers no clock, the other its own (a test's fake, say)
        var hostClock = new FakeTimeProvider();
        var withoutAClock = new ServiceCollection();
        var withAClock = new ServiceCollection();
        withAClock.AddSingleton<TimeProvider>(hostClock);

        // Act — the capture side, or the persisting side
        AddAuditSide(new ConduitBuilder(withoutAClock), capture);
        AddAuditSide(new ConduitBuilder(withAClock), capture);

        // Assert
        using var withoutAClockProvider = withoutAClock.BuildServiceProvider();
        using var withAClockProvider = withAClock.BuildServiceProvider();
        withoutAClockProvider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
        withAClockProvider.GetRequiredService<TimeProvider>().Should().BeSameAs(hostClock);
    }

    [TestMethod]
    public void RegisterThePublisherBeforeTheBackgroundDispatcher()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddConduit(conduit => conduit.AddAudit());

        // Assert — a host stops its hosted services in reverse order, so the dispatcher's queued fire-and-forget work
        // has run, and sealed its records, before the publisher's stop drains them
        using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        var publisherIndex = hostedServices.FindIndex(service => service is AuditBatchPublisher);
        var dispatcherIndex = hostedServices.FindIndex(service => service is ConduitBackgroundDispatcher);
        publisherIndex.Should().BeGreaterThanOrEqualTo(0, "the publisher is a hosted service");
        dispatcherIndex.Should().BeGreaterThanOrEqualTo(0, "the background dispatcher is a hosted service");
        publisherIndex.Should().BeLessThan(dispatcherIndex);
    }

    private static void AddAuditSide(ConduitBuilder builder, bool capture)
    {
        if (capture)
        {
            builder.AddAudit();
        }
        else
        {
            builder.AddAuditListener();
        }
    }
}

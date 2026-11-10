using AwesomeAssertions;
using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

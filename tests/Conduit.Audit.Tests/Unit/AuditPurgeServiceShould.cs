using AwesomeAssertions;
using Conduit.Audit.Configuration;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Audit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Audit.Tests.Unit;

[TestClass]
public sealed class AuditPurgeServiceShould
{
    [TestMethod]
    public async Task PurgeByTheInjectedClock()
    {
        // Arrange — a clock years away from the machine's, and a thirty-day retention
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new PurgeRecordingAuditStore();
        var services = new ServiceCollection();
        services.AddScoped<IAuditStore>(_ => store);
        await using var provider = services.BuildServiceProvider();
        using var purge = new AuditPurgeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuditOptions { RetentionPeriod = TimeSpan.FromDays(30) }),
            new FakeTimeProvider(now));

        // Act — the first pass runs as the service starts
        await purge.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.Cutoffs.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await purge.StopAsync(CancellationToken.None);

        // Assert
        store.Cutoffs.Should().NotBeEmpty().And.AllBeEquivalentTo(now.UtcDateTime - TimeSpan.FromDays(30));
    }
}

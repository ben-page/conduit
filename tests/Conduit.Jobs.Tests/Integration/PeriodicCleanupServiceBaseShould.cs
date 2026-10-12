using AwesomeAssertions;
using Conduit.Jobs.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>
/// <c>Conduit.Persistence.PeriodicCleanupServiceBase</c>, the base of <c>JobCleanup</c>,
/// <c>AuditPurgeService</c> and <c>InboxCleanup</c>: one failed pass is logged and the next comes round
/// on the interval. Tested here because <c>JobCleanup</c> is where it bit — a purge chosen as a deadlock
/// victim stopped a host whose <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c>, the default.
/// </summary>
[TestClass]
public class PeriodicCleanupServiceBaseShould
{
    [TestMethod]
    public async Task LogAFailedPass_AndRunTheNextOne_WithoutStoppingTheHost()
    {
        // Arrange
        var logs = new CapturingLoggerProvider();

        // Act
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton<FirstPassFailingCleanup>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<FirstPassFailingCleanup>());
        });

        // Assert
        await host.GetRequiredService<FirstPassFailingCleanup>().SecondPass.WaitAsync(TimeSpan.FromSeconds(5));
        var logged = await logs.WaitForWarningAsync(message => message.Contains("cleanup pass failed", StringComparison.Ordinal));
        logged.Should().Contain(nameof(FirstPassFailingCleanup));
        host.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested
            .Should().BeFalse("one failed pass is not the host's to stop for");
    }
}

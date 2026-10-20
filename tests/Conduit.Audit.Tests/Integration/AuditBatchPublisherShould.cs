using AwesomeAssertions;
using Conduit.Audit.Tests.Support;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Audit.Tests.Integration;

/// <summary>
/// An <see cref="Conduit.Audit.Runtime.AuditBatch"/> listener's failure is the drain loop's to log: it never escapes the
/// background service, so it neither stops the host nor ends audit for the rest of the process.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AuditBatchPublisherShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task KeepTheHostRunningAndPublishTheNextBatchWhenAListenerThrows()
    {
        // Arrange
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<MessageIdCapture>();
            builder.Services.AddSingleton<AuditBatchFailures>();
            builder.Services.AddConduit(conduit => conduit.AddAudit(audit => audit.BatchSize = 1));
        });
        var mediator = host.GetRequiredService<IMediator>();
        var failures = host.GetRequiredService<AuditBatchFailures>();
        var lifetime = host.GetRequiredService<IHostApplicationLifetime>();

        // Act -- the first command's batch fails its listener; the second command's batch must still be published
        await mediator.ExecuteAsync(new AuditTestCommand("first"));
        await failures.WaitForCountAsync(1);
        await mediator.ExecuteAsync(new AuditTestCommand("second"));
        await failures.WaitForCountAsync(2);

        // Assert
        lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse(
            "a failed audit listener must never stop the host");
    }
}

using AwesomeAssertions;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Audit.Tests.Support;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class AuditEndToEndShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task CaptureFullDispatchLifecycleInLocalStore()
    {
        // Arrange
        var dbPath = Path.Combine(Path.GetTempPath(), $"audit-test-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";

        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<MessageIdCapture>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddAudit(audit =>
                {
                    audit.BatchSize = 1;
                    audit.ServiceName = "TestService";
                });
                conduit.AddAuditListener(listener =>
                {
                    listener.UseSqlite(connectionString);
                });
            });
        });

        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<IAuditStore>();
        var capture = host.GetRequiredService<MessageIdCapture>();

        // Act
        var result = await mediator.ExecuteAsync(new AuditTestCommand("hello-audit"));

        // The handler captured the MessageId from MessageContext.Current
        var messageId = capture.MessageId;
        messageId.Should().NotBeNullOrEmpty("the handler should have captured the MessageId from MessageContext");

        // Wait for the background pipeline to drain:
        // observer -> channel -> AuditBatchPublisher -> mediator.PublishAsync(AuditBatch)
        //   -> LocalAuditListener -> IAuditStore
        DispatchAuditRecord? record = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            record = await store.GetByMessageIdAsync(messageId!, CancellationToken.None);
            if (record is not null)
            {
                break;
            }

            await Task.Delay(100);
        }

        // Assert
        result.Should().Be("hello-audit");

        record.Should().NotBeNull("the audit record should have been persisted by the background pipeline");
        record!.MessageId.Should().Be(messageId);
        record.MessageType.Should().Be("Conduit.Audit.Tests.Support.AuditTestCommand");
        record.MessageKind.Should().Be("command");
        record.ServiceName.Should().Be("TestService");
        record.ConversationId.Should().NotBeNullOrEmpty();

        record.Observations.Should().HaveCountGreaterThanOrEqualTo(2);
        record.Observations[0].Should().BeOfType<DispatchInvoked>();
        record.Observations[^1].Should().BeOfType<DispatchCompleted>();

        var invoked = (DispatchInvoked)record.Observations[0];
        invoked.MessageType.Should().Be("Conduit.Audit.Tests.Support.AuditTestCommand");
        invoked.MessageKind.Should().Be("command");

        var completed = (DispatchCompleted)record.Observations[^1];
        completed.DurationMs.Should().BeGreaterThanOrEqualTo(0);

        // Payload should be captured (not DoNotAudit)
        record.Payload.Should().NotBeNull();
        record.Payload!.Value.GetProperty("value").GetString().Should().Be("hello-audit");

        // Cleanup temp file
        try
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}

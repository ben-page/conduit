using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;
using global::RabbitMQ.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Tests dead letter queue capture and replay over RabbitMQ.
/// Verifies that failed handlers result in a DLQ entry, that the message is
/// acknowledged on the broker (not redelivered), and that replay re-publishes
/// the message to the inbound path.
///
/// Uses a two-host pattern (publisher + handler) so local dispatch on the
/// publisher doesn't interfere with wire-side capture on the handler.
/// </summary>
[TestClass]
public class RabbitMqDeadLetterShould : RabbitMqTestBase
{
    [TestMethod]
    public async Task CaptureFailedEventToDeadLetterStore()
    {
        // Arrange
        var handlerName = UniqueAppName("dlq-h");
        var publisherName = UniqueAppName("dlq-p");
        var dbPath = CreateDeadLetterDatabasePath();

        try
        {
            await using var handler = await CreateHostWithDeadLettersAsync(
                handlerName,
                dbPath,
                configureHandling: handling =>
                {
                    handling.ClearCommandHandlers();
                    handling.ClearQueryHandlers();
                });

            var prefix = handler.TopologyPrefix!;

            await using var publisher = await CreateHostAsync(
                publisherName,
                prefix,
                handling => handling.ClearAllHandlers());

            var mediator = publisher.GetRequiredService<IMediator>();
            var handlerSpy = handler.GetRequiredService<SpyLog>();
            var deadLetters = handler.GetRequiredService<IDeadLetters>();

            // Act — publish event; handler listener throws, capture runs
            await mediator.PublishAsync(new ThrowingTestEvent("boom"));

            await handlerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(10));

            // Assert — DLQ entry has wrapping EventListenerException (with inner InvalidOperationException)
            var page = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            page.Entries.Should().HaveCount(1);
            var entry = page.Entries[0];
            entry.MessageType.Should().Contain("ThrowingTestEvent");
            entry.ExceptionType.Should().Be("Conduit.Exceptions.EventListenerException");
            entry.SourceEndpoint.Should().Be("self");
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [TestMethod]
    public async Task AcknowledgeFailedMessageAfterCapture()
    {
        // Arrange — verify the broker does NOT redeliver after capture.
        // If capture didn't acknowledge, the listener would fire repeatedly.
        var handlerName = UniqueAppName("dlq-h");
        var publisherName = UniqueAppName("dlq-p");
        var dbPath = CreateDeadLetterDatabasePath();

        try
        {
            await using var handler = await CreateHostWithDeadLettersAsync(
                handlerName,
                dbPath,
                configureHandling: handling =>
                {
                    handling.ClearCommandHandlers();
                    handling.ClearQueryHandlers();
                });

            var prefix = handler.TopologyPrefix!;

            await using var publisher = await CreateHostAsync(
                publisherName,
                prefix,
                handling => handling.ClearAllHandlers());

            var mediator = publisher.GetRequiredService<IMediator>();
            var handlerSpy = handler.GetRequiredService<SpyLog>();
            var deadLetters = handler.GetRequiredService<IDeadLetters>();

            // Act
            await mediator.PublishAsync(new ThrowingTestEvent("once"));
            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(10));

            // Give the broker time to possibly redeliver — if it did, the listener would fire again
            await Task.Delay(2000);

            // Assert — exactly one handler invocation, one DLQ entry, no redelivery
            handlerSpy.Count.Should().Be(1, "capture should have acknowledged, preventing redelivery");
            var page = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            page.Entries.Should().HaveCount(1);
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [TestMethod]
    public async Task ReplayDeadLetteredMessageBackToBroker()
    {
        // Arrange — capture a failed message, then replay it.
        // Since the listener always throws, the replayed message will be re-captured.
        // We verify the replay went through the broker by watching for a new handler invocation.
        var handlerName = UniqueAppName("dlq-h");
        var publisherName = UniqueAppName("dlq-p");
        var dbPath = CreateDeadLetterDatabasePath();

        try
        {
            await using var handler = await CreateHostWithDeadLettersAsync(
                handlerName,
                dbPath,
                configureHandling: handling =>
                {
                    handling.ClearCommandHandlers();
                    handling.ClearQueryHandlers();
                });

            var prefix = handler.TopologyPrefix!;

            await using var publisher = await CreateHostAsync(
                publisherName,
                prefix,
                handling => handling.ClearAllHandlers());

            var mediator = publisher.GetRequiredService<IMediator>();
            var handlerSpy = handler.GetRequiredService<SpyLog>();
            var deadLetters = handler.GetRequiredService<IDeadLetters>();

            // Act — capture first failure
            await mediator.PublishAsync(new ThrowingTestEvent("replay-me"));
            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(10));

            var page = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            var entry = page.Entries.Should().ContainSingle().Subject;

            var invocationsBefore = handlerSpy.Count;

            // Replay — the listener will throw again, so a new DLQ entry will be created
            var replayResult = await deadLetters.ReplayAsync(entry.Id, CancellationToken.None);

            // Wait for the re-delivered message to be processed and re-captured
            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(10));

            // Assert — replay succeeded; handler saw a new invocation; DLQ still has one entry
            replayResult.Success.Should().BeTrue();
            handlerSpy.Count.Should().BeGreaterThan(invocationsBefore, "replay should have triggered a new delivery");
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [TestMethod]
    public async Task NotCaptureABusinessRefusal()
    {
        // Arrange — a routed command refused with NotFound (404), then one whose handler fails
        // unexpectedly. The second capture is the barrier: by the time it is stored, a capture of
        // the refusal, answered before the second command was even sent, would be stored too.
        var handlerName = UniqueAppName("dlq-h");
        var senderName = UniqueAppName("dlq-s");
        var dbPath = CreateDeadLetterDatabasePath();

        try
        {
            await using var handler = await CreateHostWithDeadLettersAsync(handlerName, dbPath);
            var prefix = handler.TopologyPrefix!;
            await using var sender = await CreateSenderHostAsync(
                senderName,
                prefix,
                typeof(ThrowingCommand).Namespace!,
                "self");

            var mediator = sender.GetRequiredService<IMediator>();
            var deadLetters = handler.GetRequiredService<IDeadLetters>();

            // Act
            var refuse = async () => await mediator.ExecuteAsync(new ThrowingCommand("missing"));
            await refuse.Should().ThrowAsync<RemoteDispatchException>();

            var fail = async () => await mediator.ExecuteAsync(new UnexpectedThrowCommand("boom"));
            var failure = await fail.Should().ThrowAsync<RemoteDispatchException>();

            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(10));

            // Assert — only the unexpected failure, under the reference its caller was given
            var page = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            var entry = page.Entries.Should().ContainSingle().Subject;
            entry.MessageType.Should().Be(typeof(UnexpectedThrowCommand).FullName);
            entry.SourceEndpoint.Should().Be("self");
            entry.Reference.Should().NotBeNullOrEmpty();
            entry.Reference.Should().Be(failure.Which.Reference);
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [TestMethod]
    public async Task CaptureAMalformedBody()
    {
        // Arrange — a message whose body is not JSON fails before it can be dispatched
        var handlerName = UniqueAppName("dlq-h");
        var dbPath = CreateDeadLetterDatabasePath();
        var body = Encoding.UTF8.GetBytes("{ this is not json");

        try
        {
            await using var handler = await CreateHostWithDeadLettersAsync(
                handlerName,
                dbPath,
                configureHandling: handling =>
                {
                    handling.ClearCommandHandlers();
                    handling.ClearQueryHandlers();
                });

            var handlerSpy = handler.GetRequiredService<SpyLog>();
            var deadLetters = handler.GetRequiredService<IDeadLetters>();

            // Act — sent straight to the app queue, as a foreign producer could
            await PublishRawToAppQueueAsync(
                handler.GetRequiredService<ConduitOptions>(),
                typeof(ThrowingTestEvent).FullName!,
                body);

            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(10));

            // Assert — captured with its body as it arrived, and never dispatched
            var page = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            var entry = page.Entries.Should().ContainSingle().Subject;
            entry.MessageType.Should().Be(typeof(ThrowingTestEvent).FullName);
            entry.ExceptionType.Should().Be(typeof(JsonException).FullName);
            entry.Body.ToArray().Should().Equal(body);
            entry.SourceEndpoint.Should().Be("self");
            handlerSpy.Count.Should().Be(0);
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [TestMethod]
    public async Task ReplayWithTheOriginalIds()
    {
        // Arrange — publish under a two-level context, so the event's MessageId, CausationId and
        // ConversationId are three distinct values the replay must carry back.
        var handlerName = UniqueAppName("dlq-h");
        var publisherName = UniqueAppName("dlq-p");
        var dbPath = CreateDeadLetterDatabasePath();

        try
        {
            await using var handler = await CreateHostWithDeadLettersAsync(
                handlerName,
                dbPath,
                configureHandling: handling =>
                {
                    handling.ClearCommandHandlers();
                    handling.ClearQueryHandlers();
                });

            await using var publisher = await CreateHostAsync(
                publisherName,
                handler.TopologyPrefix!,
                handling => handling.ClearAllHandlers());

            var mediator = publisher.GetRequiredService<IMediator>();
            var deadLetters = handler.GetRequiredService<IDeadLetters>();

            MessageContext.StartDispatch();
            var conversationRootId = MessageContext.Current!.MessageId;
            MessageContext.StartDispatch();
            var immediateCauseId = MessageContext.Current!.MessageId;
            await mediator.PublishAsync(new ThrowingTestEvent("replay-ids"));

            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(10));
            var firstPage = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            var original = firstPage.Entries.Should().ContainSingle().Subject;

            // Act — the listener throws again, so the replayed delivery is captured anew. The
            // replay runs inside this test's context, which a transport must not send instead.
            var replay = await deadLetters.ReplayAsync(original.Id, CancellationToken.None);
            var recaptured = await WaitForEntryOtherThanAsync(deadLetters, original.Id, TimeSpan.FromSeconds(10));

            // Assert
            replay.Success.Should().BeTrue();
            original.CausationId.Should().Be(immediateCauseId);
            original.ConversationId.Should().Be(conversationRootId);
            original.MessageId.Should().NotBe(immediateCauseId);
            recaptured.MessageId.Should().Be(original.MessageId);
            recaptured.CausationId.Should().Be(original.CausationId);
            recaptured.ConversationId.Should().Be(original.ConversationId);
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    private static async Task PublishRawToAppQueueAsync(ConduitOptions options, string messageType, byte[] body)
    {
        var factory = new ConnectionFactory { Uri = new Uri(ConnectionString!) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var properties = new BasicProperties
        {
            Type = messageType,
            MessageId = Guid.NewGuid().ToString("N"),
        };

        // The default exchange routes by queue name, straight to the app queue
        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: TopologyNaming.CreateAppResourceName(options, 255),
            mandatory: false,
            basicProperties: properties,
            body: body);
    }

    private static async Task<DeadLetterEntry> WaitForEntryOtherThanAsync(
        IDeadLetters deadLetters,
        Guid excludedId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var page = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            var entry = page.Entries.FirstOrDefault(candidate => candidate.Id != excludedId);
            if (entry is not null)
            {
                return entry;
            }

            await Task.Delay(100);
        }

        throw new AssertFailedException(
            $"Timed out waiting for a dead letter entry other than {excludedId} after {timeout.TotalSeconds}s");
    }

    private static async Task WaitForDeadLetterCountAsync(
        IDeadLetters deadLetters,
        int expectedCount,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var page = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            if (page.Entries.Count >= expectedCount)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out waiting for {expectedCount} dead letter entries after {timeout.TotalSeconds}s");
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            // Best-effort cleanup
        }
    }
}

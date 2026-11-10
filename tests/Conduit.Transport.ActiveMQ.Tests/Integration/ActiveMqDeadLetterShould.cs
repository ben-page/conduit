using System.Text;
using System.Text.Json;
using Amqp;
using Amqp.Framing;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests dead letter queue capture and replay over ActiveMQ Artemis.
/// </summary>
[TestClass]
public class ActiveMqDeadLetterShould : ActiveMQTestBase
{
    [TestMethod]
    public async Task CaptureFailedEventToDeadLetterStore()
    {
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

            await mediator.PublishAsync(new ThrowingTestEvent("boom"));
            await handlerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(15));
            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(15));

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

            await mediator.PublishAsync(new ThrowingTestEvent("once"));
            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(15));
            await Task.Delay(3000);

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

            await mediator.PublishAsync(new ThrowingTestEvent("replay-me"));
            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(15));

            var page = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            var entry = page.Entries.Should().ContainSingle().Subject;

            var invocationsBefore = handlerSpy.Count;

            var replayResult = await deadLetters.ReplayAsync(entry.Id, CancellationToken.None);
            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(15));

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

            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(15));

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

            // Act — sent straight to the app event queue, as a foreign producer could
            await SendRawToAppEventQueueAsync(
                handler.GetRequiredService<ConduitOptions>(),
                typeof(ThrowingTestEvent).FullName!,
                body);

            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(15));

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

            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(15));
            var firstPage = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            var original = firstPage.Entries.Should().ContainSingle().Subject;

            // Act — the listener throws again, so the replayed delivery is captured anew. The
            // replay runs inside this test's context, which a transport must not send instead.
            var replay = await deadLetters.ReplayAsync(original.Id, CancellationToken.None);
            var recaptured = await WaitForEntryOtherThanAsync(deadLetters, original.Id, TimeSpan.FromSeconds(15));

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

    [TestMethod]
    public async Task ReplayAPooledEventToItsPoolQueue()
    {
        // Arrange — ThrowingTestEvent belongs to a pool, so it reaches the application on the pool's queue:
        // the application's own event queue filters it out
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
                },
                configureOptions: options => options.AddPool(
                    "throwing", pool => pool.Handle(typeof(ThrowingTestEvent).FullName!)));

            await using var publisher = await CreateHostAsync(
                publisherName,
                handler.TopologyPrefix!,
                handling => handling.ClearAllHandlers());

            var handlerSpy = handler.GetRequiredService<SpyLog>();
            var deadLetters = handler.GetRequiredService<IDeadLetters>();

            await publisher.GetRequiredService<IMediator>().PublishAsync(new ThrowingTestEvent("pooled-replay"));
            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(15));
            var firstPage = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            var original = firstPage.Entries.Should().ContainSingle().Subject;

            // Act — the listener throws again, so the replayed delivery is captured anew
            var replay = await deadLetters.ReplayAsync(original.Id, CancellationToken.None);
            var recaptured = await WaitForEntryOtherThanAsync(deadLetters, original.Id, TimeSpan.FromSeconds(15));

            // Assert — the replay reached the listener: it was sent to the queue that takes its type
            replay.Success.Should().BeTrue();
            recaptured.MessageType.Should().Be(typeof(ThrowingTestEvent).FullName);
            handlerSpy.Invocations.OfType<ThrowingTestEvent>().Should().HaveCount(2);
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task RouteAFailedEventToTheDeadLetterAddressWithoutAConduitDlq()
    {
        // Arrange — a listener that always throws, and no Conduit dead-letter store to capture it
        var handlerName = UniqueAppName("dla-h");
        var publisherName = UniqueAppName("dla-p");

        await using var handler = await CreateHostAsync(handlerName);
        await using var publisher = await CreateHostAsync(
            publisherName, handler.TopologyPrefix!, handling => handling.ClearAllHandlers());

        var handlerSpy = handler.GetRequiredService<SpyLog>();
        var appEventQueueName = TopologyNaming.CreateAppResourceName(handler.GetRequiredService<ConduitOptions>(), 256);
        await using var management = await ArtemisManagement.ConnectAsync(ConnectionString!);

        // Act
        await publisher.GetRequiredService<IMediator>().PublishAsync(new ThrowingTestEvent("undeliverable"));
        await WaitForDeadLetterAddressCountAsync(management, appEventQueueName, 1, TimeSpan.FromSeconds(30));

        // Assert — each failure counted as a delivery attempt: the listener ran Artemis's default
        // max-delivery-attempts (10) times, then the broker moved the event to its dead-letter address
        handlerSpy.Invocations.OfType<ThrowingTestEvent>().Should().HaveCount(10)
            .And.AllSatisfy(@event => @event.Value.Should().Be("undeliverable"));
    }

    /// <summary>
    /// Waits until the broker's dead-letter queue (<c>DLQ</c>, the dead-letter address the stock broker
    /// configures for every address) holds <paramref name="expectedCount"/> messages moved there from
    /// <paramref name="sourceQueueName"/>.
    /// </summary>
    private static async Task WaitForDeadLetterAddressCountAsync(
        ArtemisManagement management,
        string sourceQueueName,
        long expectedCount,
        TimeSpan timeout)
    {
        var filter = $"_AMQ_ORIG_QUEUE = '{sourceQueueName}'";
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await management.CountMessagesAsync("DLQ", filter) >= expectedCount)
            {
                return;
            }

            await Task.Delay(200);
        }

        Assert.Fail($"Timed out waiting for {expectedCount} message(s) from '{sourceQueueName}' on DLQ after {timeout.TotalSeconds}s");
    }

    private static async Task SendRawToAppEventQueueAsync(ConduitOptions options, string messageType, byte[] body)
    {
        var connection = await new ConnectionFactory().CreateAsync(new Address(ConnectionString));
        try
        {
            var session = new Session(connection);

            // The app's event queue on the multicast address, by its fully qualified name
            var multicastAddress = TopologyNaming.CreateSharedResourceName(options, "events", 256);
            var appEventQueueName = TopologyNaming.CreateAppResourceName(options, 256);
            var sender = new SenderLink(session, $"raw-{Guid.NewGuid():N}", $"{multicastAddress}::{appEventQueueName}");

            using var message = new Message(body)
            {
                Properties = new Properties
                {
                    Subject = messageType,
                    MessageId = Guid.NewGuid().ToString("N"),
                },
            };

            await sender.SendAsync(message);
            await sender.CloseAsync();
        }
        finally
        {
            await connection.CloseAsync();
        }
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

            await Task.Delay(200);
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

            await Task.Delay(200);
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

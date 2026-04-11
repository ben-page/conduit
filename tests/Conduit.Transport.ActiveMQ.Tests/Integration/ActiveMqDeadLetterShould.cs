using AwesomeAssertions;
using Conduit.Durability.DeadLetterQueue;
using Conduit.TestUtils;
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
    public async Task CaptureFailedRequestToDeadLetterStore()
    {
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

            var act = async () => await mediator.ExecuteAsync(new ThrowingCommand("bad-input"));
            await act.Should().ThrowAsync<Exception>();

            await WaitForDeadLetterCountAsync(deadLetters, 1, TimeSpan.FromSeconds(15));

            var page = await deadLetters.QueryAsync(new DeadLetterFilter(), CancellationToken.None);
            page.Entries.Should().HaveCount(1);
            page.Entries[0].MessageType.Should().Contain("ThrowingCommand");
            page.Entries[0].SourceEndpoint.Should().Be("self");
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
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

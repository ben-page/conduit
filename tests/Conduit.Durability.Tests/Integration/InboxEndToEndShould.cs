using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Durability.Configuration;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Durability.Tests.Support;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Conduit.Persistence;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Persistence.Sqlite;

namespace Conduit.Durability.Tests.Integration;

[TestClass]
public class InboxEndToEndShould
{
    [TestMethod]
    public async Task DeduplicateWireRequest()
    {
        // Arrange — shared in-memory connection so all components see the same DB
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;

        var store = new DbConnectionInboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var handler = new CountHandler();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton(handler);
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability =>
                {
                    durability.AddInbox();
                });
            });

            // Override with shared connection
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
        });

        var wireMediator = host.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(
            "Conduit.Durability.Tests.Support.CountCommand",
            JsonSerializer.SerializeToElement(new { Label = "dedup" }));

        // Act — dispatch same message twice with same MessageId
        var previous = MessageContext.Current;
        try
        {
            MessageContext.StartDispatchFromTransport("msg-dedup-001", null, "conv-001");
            var result1 = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

            MessageContext.StartDispatchFromTransport("msg-dedup-001", null, "conv-001");
            var result2 = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);

            // Assert — handler ran only once (first call returns 1, second returns null)
            result1.Error.Should().BeNull();
            result1.Data.Should().NotBeNull();
            result2.Data.Should().BeNull("duplicate was silently discarded");
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }

    [TestMethod]
    public async Task AllowDistinctMessages()
    {
        // Arrange
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;

        var store = new DbConnectionInboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var handler = new CountHandler();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton(handler);
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability =>
                {
                    durability.AddInbox();
                });
            });

            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
        });

        var wireMediator = host.GetRequiredService<IWireMediator>();

        // Act — dispatch two different messages
        var previous = MessageContext.Current;
        try
        {
            MessageContext.StartDispatchFromTransport("msg-001", null, "conv-001");
            var envelope1 = new DispatchEnvelope(
                "Conduit.Durability.Tests.Support.CountCommand",
                JsonSerializer.SerializeToElement(new { Label = "first" }));
            var result1 = await wireMediator.HandleWireRequestAsync(envelope1, CancellationToken.None);

            MessageContext.StartDispatchFromTransport("msg-002", null, "conv-001");
            var envelope2 = new DispatchEnvelope(
                "Conduit.Durability.Tests.Support.CountCommand",
                JsonSerializer.SerializeToElement(new { Label = "second" }));
            var result2 = await wireMediator.HandleWireRequestAsync(envelope2, CancellationToken.None);

            // Assert — both handlers ran
            result1.Data.Should().NotBeNull();
            result2.Data.Should().NotBeNull();
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }

    // The broker redelivers what a failed listener left abandoned, under the same MessageId. Were the failed
    // attempt's record kept, the redelivery would be discarded as a duplicate and acknowledged: one transient
    // failure, and the message lost with no dead-letter entry.
    [TestMethod]
    public async Task LetARedeliveryRunAfterTheFirstAttemptFailed()
    {
        // Arrange — the inbox alone, no dead letter queue: the first attempt fails and is abandoned
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        var inboxStore = new DbConnectionInboxStore(connectionFactory, new SqliteDialect());
        await inboxStore.EnsureTableExistsAsync(CancellationToken.None);

        var deliveries = new DeliveryCounter();
        await using var host = StartBrokerHost(connectionFactory, deliveries, durability => durability.AddInbox());
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);

        var firstAttempt = BrokerMessage(new FlakyInboxEvent(Failures: 1), "msg-redelivered", isRequest: false);
        await transport.DeliverAsync(firstAttempt);
        var redelivery = BrokerMessage(new FlakyInboxEvent(Failures: 1), "msg-redelivered", isRequest: false);

        // Act
        await transport.DeliverAsync(redelivery);

        // Assert — the redelivery ran the listener, and that success is what the inbox now holds
        firstAttempt.Abandoned.Should().BeTrue();
        deliveries.Count.Should().Be(2, "the redelivery ran its listener rather than being discarded as a duplicate");
        redelivery.Acknowledged.Should().BeTrue();
        (await inboxStore.ExistsAsync("msg-redelivered", CancellationToken.None)).Should().BeTrue();
    }

    // A dead-letter replay carries the original MessageId. Both error interceptors see the failure: the dead letter
    // queue stores it, the inbox releases its record, so the replay runs inside the retention period.
    [TestMethod]
    public async Task LetADeadLetterReplayRunAfterTheFirstAttemptFailed()
    {
        // Arrange — the inbox beside the dead letter queue: the failed request is captured and acknowledged
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        var inboxStore = new DbConnectionInboxStore(connectionFactory, new SqliteDialect());
        await inboxStore.EnsureTableExistsAsync(CancellationToken.None);
        var deadLetterStore = new DbDeadLetterStore(connectionFactory, new SqliteDialect());
        await deadLetterStore.EnsureTableExistsAsync(CancellationToken.None);

        var deliveries = new DeliveryCounter();
        await using var host = StartBrokerHost(
            connectionFactory,
            deliveries,
            durability => durability.AddInbox().AddDeadLetterQueue());
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);

        await transport.DeliverAsync(BrokerMessage(new FlakyInboxCommand(Failures: 1), "msg-replayed", isRequest: true));
        var entry = (await deadLetterStore.QueryAsync(new DeadLetterFilter(), CancellationToken.None)).Entries
            .Should().ContainSingle().Subject;

        // As RedeliverAsync sends it: the entry's body under its original ids, and no reply address
        var replay = new FakeBrokerMessage
        {
            Type = entry.MessageType,
            Body = entry.Body,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MessageId"] = entry.MessageId!,
                ["CausationId"] = entry.CausationId!,
                ["ConversationId"] = entry.ConversationId!,
            },
        };

        // Act
        await transport.DeliverAsync(replay);

        // Assert — the replay ran its handler under the original id
        entry.MessageId.Should().Be("msg-replayed");
        deliveries.Count.Should().Be(2, "the replay ran its handler rather than being discarded as a duplicate");
        replay.Acknowledged.Should().BeTrue();
    }

    // Gates run before the body is read, so the gate has recorded the id of an event whose body its type cannot
    // read by the time the read fails. That failure releases it as any other does: the replay of the captured
    // event, once the contract is fixed, runs.
    [TestMethod]
    public async Task ReleaseTheRecordOfAnEventItsTypeCannotRead()
    {
        // Arrange — the inbox alone; a field of the wrong type
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        var inboxStore = new DbConnectionInboxStore(connectionFactory, new SqliteDialect());
        await inboxStore.EnsureTableExistsAsync(CancellationToken.None);

        var deliveries = new DeliveryCounter();
        await using var host = StartBrokerHost(connectionFactory, deliveries, durability => durability.AddInbox());
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var unreadable = new FakeBrokerMessage
        {
            Type = typeof(FlakyInboxEvent).FullName!,
            Body = new BinaryData("""{"failures":"one"}"""),
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MessageId"] = "msg-unreadable",
                ["ConversationId"] = "msg-unreadable",
            },
        };

        // Act
        await transport.DeliverAsync(unreadable);

        // Assert — acknowledged uncaptured (a redelivery would fail the same way), no listener ran, no record kept
        unreadable.Acknowledged.Should().BeTrue();
        deliveries.Count.Should().Be(0);
        (await inboxStore.ExistsAsync("msg-unreadable", CancellationToken.None)).Should().BeFalse();
    }

    // A failure before the gate ran (here a body that is not JSON) recorded nothing, so it releases nothing: the
    // record is the delivery's that succeeded, and removing it would let a later duplicate run the listener again.
    [TestMethod]
    public async Task KeepTheRecordOfASucceededDeliveryWhenADuplicateFailsBeforeDispatch()
    {
        // Arrange — the first delivery succeeded and is recorded
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        var inboxStore = new DbConnectionInboxStore(connectionFactory, new SqliteDialect());
        await inboxStore.EnsureTableExistsAsync(CancellationToken.None);

        var deliveries = new DeliveryCounter();
        await using var host = StartBrokerHost(connectionFactory, deliveries, durability => durability.AddInbox());
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        await transport.DeliverAsync(BrokerMessage(new FlakyInboxEvent(Failures: 0), "msg-succeeded", isRequest: false));
        var malformed = new FakeBrokerMessage
        {
            Type = typeof(FlakyInboxEvent).FullName!,
            Body = new BinaryData("not json"),
            Properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MessageId"] = "msg-succeeded",
                ["ConversationId"] = "msg-succeeded",
            },
        };

        // Act
        await transport.DeliverAsync(malformed);

        // Assert — settled before dispatch, the succeeded delivery's record intact
        malformed.Acknowledged.Should().BeTrue();
        deliveries.Count.Should().Be(1);
        (await inboxStore.ExistsAsync("msg-succeeded", CancellationToken.None)).Should().BeTrue();
    }

    // A gate whose store is down decided nothing: it recorded nothing and no listener ran. The event must reach the
    // broker again, not be acknowledged as handled (it used to be: logged, never recorded, never redelivered), and the
    // redelivery, once the database is back, runs its listener.
    [TestMethod]
    public async Task AbandonAnEventWhoseInboxStoreFailsAtTheGateAndRunItsRedelivery()
    {
        // Arrange -- the inbox alone, over a store whose database is down
        var inboxStore = new InMemoryInboxStore { RecordFailure = new InvalidOperationException("The database is down.") };
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        var deliveries = new DeliveryCounter();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton(deliveries);

            // Ahead of AddInbox, whose TryAdd then keeps it
            builder.Services.AddSingleton<IInboxStore>(inboxStore);
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddInbox());
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
        });
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        var firstAttempt = BrokerMessage(new FlakyInboxEvent(Failures: 0), "msg-store-down", isRequest: false);

        // Act
        await transport.DeliverAsync(firstAttempt);
        inboxStore.RecordFailure = null;
        var redelivery = BrokerMessage(new FlakyInboxEvent(Failures: 0), "msg-store-down", isRequest: false);
        await transport.DeliverAsync(redelivery);

        // Assert -- the first attempt abandoned unhandled, the redelivery handled and recorded
        firstAttempt.Abandoned.Should().BeTrue();
        firstAttempt.Acknowledged.Should().BeFalse();
        redelivery.Acknowledged.Should().BeTrue();
        deliveries.Count.Should().Be(1, "the listener ran once, for the redelivery");
        (await inboxStore.ExistsAsync("msg-store-down", CancellationToken.None)).Should().BeTrue();
    }

    private static TestWebHost StartBrokerHost(
        StubConnectionFactory connectionFactory,
        DeliveryCounter deliveries,
        Action<DurabilityBuilder> configureDurability) => TestWebHost.Start(builder =>
    {
        builder.Services.AddSingleton(deliveries);
        builder.Services.AddConduit(conduit =>
        {
            conduit.UseSqlite("Data Source=:memory:");
            conduit.AddDurability(configureDurability);
        });

        // Override with the shared in-memory connection, so the test's stores see the host's rows
        builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
    });

    private static FakeBrokerMessage BrokerMessage(object message, string messageId, bool isRequest) => new()
    {
        Type = message.GetType().FullName!,
        Body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), ConduitRuntime.JsonOptions)),
        IsRequest = isRequest,
        Properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MessageId"] = messageId,
            ["ConversationId"] = messageId,
        },
    };
}

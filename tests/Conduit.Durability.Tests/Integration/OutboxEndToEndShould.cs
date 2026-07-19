using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Conduit.Persistence;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence.Sqlite;

namespace Conduit.Durability.Tests.Integration;

[TestClass]
public class OutboxEndToEndShould
{
    [TestMethod]
    public async Task PersistPublishedEventToOutboxTable()
    {
        // Arrange
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;

        // Create the outbox table using the store (same pattern as unit tests)
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability =>
                {
                    durability.AddOutbox(options => options.AutoCreateTable = false);
                });
            });

            // Override the connection factory with our shared in-memory connection
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
        });

        var mediator = host.GetRequiredService<IMediator>();

        // Begin a transaction (simulating what a user's handler would do)
        await connectionFactory.BeginTransactionAsync();

        // Act
        var orderId = await mediator.ExecuteAsync(new PlaceOrderCommand("Widget"), CancellationToken.None);

        // Commit so we can query
        await connectionFactory.CommitAsync();

        // Assert
        using var queryCommand = connectionFactory.Connection.CreateCommand();
        queryCommand.CommandText = "SELECT Type, Payload, MessageId, ConversationId FROM conduit_outbox";
        using var reader = await queryCommand.ExecuteReaderAsync();

        var entries = new List<(string Type, string Payload, string MessageId, string ConversationId)>();
        while (await reader.ReadAsync())
        {
            entries.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        entries.Should().HaveCount(1);
        entries[0].Type.Should().Contain("OrderPlacedEvent");
        entries[0].Payload.Should().Contain("Widget");
        entries[0].MessageId.Should().NotBeNullOrEmpty();
        entries[0].ConversationId.Should().NotBeNullOrEmpty();
        orderId.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task FlushAsyncSendsCapturedEventAndMarksEntrySent()
    {
        // Arrange
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;

        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var spyTransport = new SpyTransport();

        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability =>
                {
                    durability.AddOutbox(options => options.AutoCreateTable = false);
                });
            });

            // Override the connection factory with our shared in-memory connection
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);

            // Register a spy transport so FlushAsync can send through it
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
        });

        var mediator = host.GetRequiredService<IMediator>();

        // Begin a transaction (simulating what a user's handler would do)
        await connectionFactory.BeginTransactionAsync();

        // Act — execute the command (handler publishes event → captured → written to outbox)
        var orderId = await mediator.ExecuteAsync(new PlaceOrderCommand("Gadget"), CancellationToken.None);

        // Commit the transaction (business data + outbox entries, atomic)
        await connectionFactory.CommitAsync();

        // Flush — schedules delivery from memory on the background dispatcher and returns
        var flusher = host.GetRequiredService<IOutboxFlusher>();
        await flusher.FlushAsync(CancellationToken.None);

        // Assert — THE contract pin: FlushAsync must not have delivered yet when it returns.
        spyTransport.SentEvents.Should().BeEmpty("FlushAsync must schedule background delivery and return without waiting for it");

        // The background dispatcher drains the enqueued work item on its own hosted-service loop
        // (TestWebHost starts it for real) — bounded-poll for the terminal DB state. MarkSentAsync
        // is the LAST step of the delivery routine, strictly after the transport send, so once Sent
        // is observed the transport assertion below cannot be racing the still-in-flight work item.
        using var queryCommand = connectionFactory.Connection.CreateCommand();
        queryCommand.CommandText = "SELECT Status FROM conduit_outbox";

        var sent = false;
        for (var attempt = 0; attempt < 50 && !sent; attempt++)
        {
            using var reader = await queryCommand.ExecuteReaderAsync();
            var statuses = new List<int>();
            while (await reader.ReadAsync())
            {
                statuses.Add(reader.GetInt32(0));
            }

            sent = statuses.Count == 1 && statuses[0] == (int)OutboxEntryStatus.Sent;
            await reader.CloseAsync();

            if (!sent)
            {
                await Task.Delay(100);
            }
        }

        sent.Should().BeTrue("the background dispatcher must eventually deliver the flushed entry and mark it sent");

        // Assert — transport received the event
        spyTransport.SentEvents.Should().HaveCount(1);
        spyTransport.SentEvents[0].Type.Should().Contain("OrderPlacedEvent");
        orderId.Should().NotBeEmpty();
    }
}

using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Outbox;
using Conduit.Outbox.Dialects;
using Conduit.Outbox.Runtime;
using Conduit.Outbox.Store;
using Conduit.Outbox.Tests.Support;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Outbox.Tests.Integration;

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
                conduit.AddOutbox(outbox =>
                {
                    outbox.UseSqlite("Data Source=:memory:");
                    outbox.WithAutoCreateTable(false);
                });
            });

            // Override the connection factory with our shared in-memory connection
            builder.Services.AddSingleton<IOutboxConnectionFactory>(connectionFactory);
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
                conduit.AddOutbox(outbox =>
                {
                    outbox.UseSqlite("Data Source=:memory:");
                    outbox.WithAutoCreateTable(false);
                });
            });

            // Override the connection factory with our shared in-memory connection
            builder.Services.AddSingleton<IOutboxConnectionFactory>(connectionFactory);

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

        // Flush — sends from memory, marks entries as sent
        var flusher = host.GetRequiredService<IOutboxFlusher>();
        await flusher.FlushAsync(CancellationToken.None);

        // Assert — transport received the event
        spyTransport.SentEvents.Should().HaveCount(1);
        spyTransport.SentEvents[0].Type.Should().Contain("OrderPlacedEvent");

        // Assert — outbox entry is marked as Sent
        using var queryCommand = connectionFactory.Connection.CreateCommand();
        queryCommand.CommandText = "SELECT Status FROM conduit_outbox";
        using var reader = await queryCommand.ExecuteReaderAsync();

        var statuses = new List<int>();
        while (await reader.ReadAsync())
        {
            statuses.Add(reader.GetInt32(0));
        }

        statuses.Should().HaveCount(1);
        statuses[0].Should().Be((int)OutboxEntryStatus.Sent);
        orderId.Should().NotBeEmpty();
    }
}

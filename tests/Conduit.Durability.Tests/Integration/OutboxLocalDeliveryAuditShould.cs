using AwesomeAssertions;
using Conduit.Audit;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Exceptions;
using Conduit.Generated;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace Conduit.Durability.Tests.Integration;

[TestClass]
public class OutboxLocalDeliveryAuditShould
{
    [TestCleanup]
    public void Cleanup() => MessageContext.RestoreContext(null);

    [TestMethod]
    public async Task RecordTheFlushedDeliveryAsAnEventDispatchWithoutItsPayload()
    {
        // Arrange
        await using var connectionFactory = new StubConnectionFactory();
        await using var host = await StartHostAsync(connectionFactory, new SpyTransport());
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();

        // Act
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("kept"), CancellationToken.None);
        await connectionFactory.CommitAsync();
        await host.GetRequiredService<IOutboxFlusher>().FlushAsync(CancellationToken.None);
        await WaitForSentAsync(connectionFactory);

        // Assert
        var (outboxMessageId, outboxConversationId) = await ReadOutboxIdsAsync(connectionFactory);
        var deliveryId = probe.MessageIds.Should().ContainSingle().Subject!;
        var store = host.GetRequiredService<IAuditStore>();
        var delivery = await WaitForRecordAsync(store, deliveryId);

        delivery.MessageKind.Should().Be("Event");
        delivery.MessageType.Should().EndWith("AuditTrailEvent");
        delivery.CausationId.Should().Be(outboxMessageId);
        delivery.ConversationId.Should().Be(outboxConversationId);
        delivery.Payload.Should().BeNull("the delivery record never carries a body; the publish row holds it");
        delivery.Observations[0].Should().BeOfType<DispatchInvoked>();
        delivery.Observations[^1].Should().BeOfType<DispatchCompleted>();

        var publish = await WaitForRecordByTypeAsync(connectionFactory, store, delivery.MessageType, "event");
        publish.MessageKind.Should().Be("event");
        publish.MessageType.Should().Be(delivery.MessageType);
        publish.Payload.Should().NotBeNull();
        publish.Payload!.Value.GetProperty("detail").GetString().Should().Be("kept");
    }

    [TestMethod]
    public async Task RecordAFailedFlushAndTheRelayRedeliveryAsTwoRows()
    {
        // Arrange — the listener throws on its first call
        await using var connectionFactory = new StubConnectionFactory();
        await using var host = await StartHostAsync(connectionFactory, new SpyTransport());
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();
        probe.ThrowOnNextCalls(1);

        // Act — the flush's delivery fails (the entry stays pending), then the relay redelivers
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("retried"), CancellationToken.None);
        await connectionFactory.CommitAsync();
        await host.GetRequiredService<IOutboxFlusher>().FlushAsync(CancellationToken.None);
        await WaitForRetryReleasedAsync(connectionFactory);

        var relay = new OutboxRelay(
            host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<TransportDispatcher>(),
            Options.Create(new OutboxOptions { GracePeriod = TimeSpan.Zero, AutoCreateTable = false }),
            NullLogger<OutboxRelay>.Instance);
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert
        var (outboxMessageId, _) = await ReadOutboxIdsAsync(connectionFactory);
        var ids = probe.MessageIds;
        ids.Should().HaveCount(2);
        ids.Distinct().Should().HaveCount(2, "each attempt is a dispatch with its own MessageId");

        var store = host.GetRequiredService<IAuditStore>();
        var first = await WaitForRecordAsync(store, ids[0]!);
        var second = await WaitForRecordAsync(store, ids[1]!);

        first.CausationId.Should().Be(outboxMessageId);
        second.CausationId.Should().Be(outboxMessageId);

        // The dispatch fails with the event pipeline's wrapper; the listener's own exception type is on the record's
        // HandlerFailed observation.
        var failed = first.Observations[^1].Should().BeOfType<DispatchFailed>().Subject;
        failed.ExceptionType.Should().Be(typeof(EventListenerException).FullName);
        first.Observations.OfType<HandlerFailed>().Should().ContainSingle()
            .Which.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        second.Observations[^1].Should().BeOfType<DispatchCompleted>();
    }

    [TestMethod]
    public async Task CauseAListenersNestedDispatchByItsDeliveryRow()
    {
        // Arrange
        await using var connectionFactory = new StubConnectionFactory();
        await using var host = await StartHostAsync(connectionFactory, new SpyTransport());
        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();
        probe.ExecuteNested = true;

        // Act
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("nested"), CancellationToken.None);
        await connectionFactory.CommitAsync();
        await host.GetRequiredService<IOutboxFlusher>().FlushAsync(CancellationToken.None);
        await WaitForSentAsync(connectionFactory);

        // Assert — the nested command is caused by the delivery's record, which is caused by the entry
        var (outboxMessageId, _) = await ReadOutboxIdsAsync(connectionFactory);
        var deliveryId = probe.MessageIds.Should().ContainSingle().Subject!;
        var store = host.GetRequiredService<IAuditStore>();
        var delivery = await WaitForRecordAsync(store, deliveryId);
        delivery.MessageKind.Should().Be("Event");
        delivery.CausationId.Should().Be(outboxMessageId);

        probe.NestedMessageId.Should().NotBeNullOrEmpty();
        var nested = await WaitForRecordAsync(store, probe.NestedMessageId!);
        nested.CausationId.Should().Be(deliveryId);
        nested.CausationId.Should().NotBe(outboxMessageId, "the delivery is a dispatch of its own between the entry and the listener's work");
    }

    [TestMethod]
    public async Task RecordAnUnknownEventTypeAsAFailedDelivery()
    {
        // Arrange
        await using var connectionFactory = new StubConnectionFactory();
        await using var host = await StartHostAsync(connectionFactory, new SpyTransport());
        var dispatcher = host.GetRequiredService<ILocalEnvelopeDispatcher>();

        // Act
        MessageContext.StartDispatchFromTransport("m-2", null, "c-2");
        var act = () => dispatcher.PublishLocalEnvelopeAsync(
            new DispatchEnvelope("No.Such.Event", default), host.Services, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        var record = await WaitForRecordByTypeAsync(connectionFactory, host.GetRequiredService<IAuditStore>(), "No.Such.Event");
        record.Observations[^1].Should().BeOfType<DispatchFailed>();
        record.CausationId.Should().Be("m-2");
    }

    private static async Task<TestWebHost> StartHostAsync(StubConnectionFactory connectionFactory, SpyTransport spyTransport)
    {
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        // The relay's grace period is pushed out of reach so only the flush (and the hand-run relay) deliver.
        return TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddOutbox(options =>
                {
                    options.AutoCreateTable = false;
                    options.GracePeriod = TimeSpan.FromHours(1);
                }));
                conduit.AddAudit(audit => audit.BatchSize = 1);
                conduit.AddAuditListener(_ => { });
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
        });
    }

    private static async Task<(string MessageId, string ConversationId)> ReadOutboxIdsAsync(StubConnectionFactory connectionFactory)
    {
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT MessageId, ConversationId FROM conduit_outbox";
        using var reader = await query.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task WaitForSentAsync(StubConnectionFactory connectionFactory)
    {
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT Status FROM conduit_outbox";
        var sent = false;
        for (var attempt = 0; attempt < 50 && !sent; attempt++)
        {
            sent = Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == (int)OutboxEntryStatus.Sent;
            if (!sent)
            {
                await Task.Delay(100);
            }
        }

        sent.Should().BeTrue("the flush must deliver the entry and mark it sent");
    }

    // The failed delivery's last step releases the claim (RetryCount + 1, back to pending): wait for it so the
    // relay finds the entry.
    private static async Task WaitForRetryReleasedAsync(StubConnectionFactory connectionFactory)
    {
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT RetryCount FROM conduit_outbox";
        var released = false;
        for (var attempt = 0; attempt < 50 && !released; attempt++)
        {
            released = Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture) >= 1;
            if (!released)
            {
                await Task.Delay(100);
            }
        }

        released.Should().BeTrue("the failed flush delivery must leave the entry pending for the relay");
    }

    // Audit records reach the store through a background batch pipeline: poll for one by id.
    private static async Task<DispatchAuditRecord> WaitForRecordAsync(IAuditStore store, string messageId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var record = await store.GetByMessageIdAsync(messageId, CancellationToken.None);
            if (record is not null)
            {
                return record;
            }

            await Task.Delay(100);
        }

        throw new AssertFailedException($"no audit record arrived for {messageId}");
    }

    // The store has no query by type, so find the record's id in the audit table, then read it through the store.
    private static async Task<DispatchAuditRecord> WaitForRecordByTypeAsync(
        StubConnectionFactory connectionFactory, IAuditStore store, string messageType, string? messageKind = null)
    {
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT MessageId FROM conduit_audit WHERE MessageType = @Type AND (@Kind IS NULL OR MessageKind = @Kind)";
        query.Parameters.AddWithValue("@Type", messageType);
        query.Parameters.AddWithValue("@Kind", (object?)messageKind ?? DBNull.Value);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await query.ExecuteScalarAsync() is string messageId)
            {
                return await WaitForRecordAsync(store, messageId);
            }

            await Task.Delay(100);
        }

        throw new AssertFailedException($"no audit record arrived for type {messageType}");
    }
}

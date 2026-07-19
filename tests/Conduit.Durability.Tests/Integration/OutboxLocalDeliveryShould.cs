using AwesomeAssertions;
using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Durability.Tests.Support;
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
public class OutboxLocalDeliveryShould
{
    [TestMethod]
    public async Task RunZeroListenersWhenTheTransactionNeverCommits()
    {
        // Arrange
        await using var connectionFactory = new StubConnectionFactory();
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddOutbox(options => options.AutoCreateTable = false));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
        });

        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();

        // Act — command publishes the F&F event inside an ambient transaction, then the txn ROLLS BACK
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("doomed"), CancellationToken.None);
        await connectionFactory.RollbackAsync();

        // Give the background dispatcher time to prove the negative: nothing was ever enqueued.
        await Task.Delay(300);

        // Assert — capture skipped the local enqueue; rollback discarded the outbox row; zero deliveries
        probe.Count.Should().Be(0, "a rolled-back command's captured event must never reach a listener");
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM conduit_outbox";
        Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture).Should().Be(0);
    }

    [TestMethod]
    public async Task DeliverLocalListenersExactlyOnceOnFlushThenTransportsThenMarkSent()
    {
        // Arrange — host as in RunZeroListenersWhenTheTransactionNeverCommits, plus the spy transport
        await using var connectionFactory = new StubConnectionFactory();
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var spyTransport = new SpyTransport();

        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddOutbox(options => options.AutoCreateTable = false));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
        });

        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();

        // Act
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("kept"), CancellationToken.None);
        await connectionFactory.CommitAsync();

        probe.Count.Should().Be(0, "committed but not yet flushed — listeners must not have run");

        var flusher = host.GetRequiredService<IOutboxFlusher>();
        await flusher.FlushAsync(CancellationToken.None);

        // Assert — exactly one local delivery, the transport got the envelope, entry is Sent
        probe.Count.Should().Be(1);
        spyTransport.SentEvents.Should().ContainSingle(e => e.Type.Contains("AuditTrailEvent"));
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT Status FROM conduit_outbox";
        Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture)
            .Should().Be((int)OutboxEntryStatus.Sent);
    }

    [TestMethod]
    public async Task RedeliverThroughTheRelayWhenTheFlushNeverRan()
    {
        // Arrange — host as in DeliverLocalListenersExactlyOnceOnFlushThenTransportsThenMarkSent
        // (probe + spy transport registered); this time the relay redelivers because the flush
        // never ran (simulating a crash between commit and IOutboxFlusher.FlushAsync).
        await using var connectionFactory = new StubConnectionFactory();
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var spyTransport = new SpyTransport();

        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddOutbox(options => options.AutoCreateTable = false));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
        });

        var mediator = host.GetRequiredService<IMediator>();
        var probe = host.GetRequiredService<ListenerProbe>();

        // Act — commit but simulate a crash before IOutboxFlusher.FlushAsync
        await connectionFactory.BeginTransactionAsync();
        _ = await mediator.ExecuteAsync(new RecordAuditCommand("crashed-before-flush"), CancellationToken.None);
        await connectionFactory.CommitAsync();

        // Resolve the host's IServiceScopeFactory + singleton TransportDispatcher (not a rebuilt
        // one) so listener resolution reaches the host's ListenerProbe and the spy transport is
        // included in the dispatch.
        var relay = new OutboxRelay(
            host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<TransportDispatcher>(),
            Options.Create(new OutboxOptions { GracePeriod = TimeSpan.Zero, AutoCreateTable = false }),
            NullLogger<OutboxRelay>.Instance);
        await relay.ProcessPendingAsync(CancellationToken.None);

        // Assert — the relay delivered locally and marked sent
        probe.Count.Should().Be(1, "the relay must redeliver local listeners for entries the flush never handled");
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT Status FROM conduit_outbox";
        Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture)
            .Should().Be((int)OutboxEntryStatus.Sent);
    }
}

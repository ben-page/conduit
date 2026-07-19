using AwesomeAssertions;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.Generated;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
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
}

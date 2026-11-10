using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Durability.Tests.Integration;

/// <summary>
/// The outbox relay and the inbox cleanup query their tables on their very first pass. On a database that has
/// none yet, the table initializer must have created them first, or every cold start logs "no such table" at
/// Error.
/// </summary>
[TestClass]
public sealed class DurabilityStartupShould
{
    [TestMethod]
    public async Task NotSweepBeforeTheTablesExist()
    {
        // Arrange — a database file that does not exist yet, and a relay that sweeps every few milliseconds
        await using var connectionFactory = new CountingConnectionFactory();
        var spy = new SpyLoggerProvider();

        // Act
        await using (await TestHost.StartAsync(builder =>
        {
            builder.Logging.AddProvider(spy);
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite($"Data Source={connectionFactory.DatabasePath}");
                conduit.AddDurability(durability =>
                {
                    durability.AddOutbox(options => options.RelayInterval = TimeSpan.FromMilliseconds(10));
                    durability.AddInbox();
                    durability.AddDeadLetterQueue();
                });
            });

            // Transaction-aware as far as the outbox's startup check can tell, over the same fresh file.
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
        }))
        {
            // Several relay sweeps and the cleanup's first pass, whatever order they started in.
            await WaitForConnectionsAsync(connectionFactory, count: 30);
        }

        // Assert
        spy.Entries.Where(entry => entry.Level >= LogLevel.Error).Should().BeEmpty();
    }

    private static async Task WaitForConnectionsAsync(CountingConnectionFactory connectionFactory, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (connectionFactory.CreatedCount < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {connectionFactory.CreatedCount} connections were opened.");
            }

            await Task.Delay(10);
        }
    }
}

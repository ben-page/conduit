using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Persistence.Sqlite;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Durability.Tests.Integration;

/// <summary>
/// The transactional outbox is only meaningful with a transaction-aware connection factory.
/// With the built-in connection-string factory (the one every Use* registers by default),
/// GetCurrentTransaction always returns null, so capture would silently persist nothing.
/// Enabling the outbox on that default factory must fail fast at startup with an actionable
/// message rather than appear configured while protecting nothing.
/// </summary>
[TestClass]
public sealed class OutboxRequiresTransactionalFactoryShould
{
    [TestMethod]
    public async Task FailFastAtStartupWhenOutboxUsesTheDefaultNonTransactionalFactory()
    {
        // Act — start a host that enables the outbox on the default SQLite factory and nothing else.
        var start = async () =>
        {
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
            });
        };

        // Assert — startup throws a clear, actionable error pointing at the missing
        // transaction-aware connection factory.
        var exception = await start.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("transaction-aware");
        exception.Which.Message.Should().Contain("IConduitConnectionFactory");
    }
}

using System.Text.Json;
using AwesomeAssertions;
using Conduit.Durability.Tests.Support;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Conduit.Durability.Persistence;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Dialects;

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
                conduit.AddDurability(durability =>
                {
                    durability.UseSqlite("Data Source=:memory:");
                    durability.AddInbox();
                });
            });

            // Override with shared connection
            builder.Services.AddSingleton<IDurabilityConnectionFactory>(connectionFactory);
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
                conduit.AddDurability(durability =>
                {
                    durability.UseSqlite("Data Source=:memory:");
                    durability.AddInbox();
                });
            });

            builder.Services.AddSingleton<IDurabilityConnectionFactory>(connectionFactory);
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
}

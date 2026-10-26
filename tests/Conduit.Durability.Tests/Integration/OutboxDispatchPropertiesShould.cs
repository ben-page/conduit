using System.Globalization;
using System.Text.Json;
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

namespace Conduit.Durability.Tests.Integration;

/// <summary>
/// The dispatch properties a host lists in <see cref="OutboxOptions.PersistedProperties"/> ride the outbox entry, and
/// both delivery paths (the post-commit flush and the relay) restore them on the listener's context.
/// </summary>
[TestClass]
public class OutboxDispatchPropertiesShould
{
    private static readonly Guid DeviceId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    [TestCleanup]
    public void Cleanup() => MessageContext.RestoreContext(null);

    [TestMethod]
    public async Task PersistOnlyTheListedPropertiesWithTheirTypes()
    {
        // Arrange
        await using var connectionFactory = new StubConnectionFactory();
        await using var host = await StartHostAsync(connectionFactory);

        // Act
        await PublishAsync(host, connectionFactory, Properties(("a", "alpha"), ("b", DeviceId), ("c", 42)));

        // Assert — the column holds a JSON object of the listed keys only.
        var stored = await ReadPropertiesColumnAsync(connectionFactory);
        using (var document = JsonDocument.Parse(stored!))
        {
            document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("a", "b");
            document.RootElement.GetProperty("a").GetString().Should().Be("alpha");
            document.RootElement.GetProperty("b").GetGuid().Should().Be(DeviceId);
        }

        // ...and it round-trips through the store's claim, back to the declared types.
        var store = new DbConnectionOutboxStore(connectionFactory, new SqliteDialect(), Options.Create(ListedOptions()));
        var entry = (await store.ClaimPendingAsync(10, TimeSpan.Zero, CancellationToken.None)).Should().ContainSingle().Subject;
        entry.Properties.Should().Be(stored);

        var restored = OutboxProperties.Read(entry.Properties, ListedOptions().PersistedProperties);
        restored.Should().NotBeNull();
        restored!.Keys.Should().BeEquivalentTo("a", "b");
        restored["a"].Should().BeOfType<string>().Which.Should().Be("alpha");
        restored["b"].Should().BeOfType<Guid>().Which.Should().Be(DeviceId);
    }

    [TestMethod]
    public async Task WriteNullWhenNoListedPropertyIsPresent()
    {
        // Arrange
        await using var connectionFactory = new StubConnectionFactory();
        await using var host = await StartHostAsync(connectionFactory);

        // Act — only an unlisted key, and a listed key whose value is null.
        await PublishAsync(host, connectionFactory, Properties(("c", 42), ("a", null)));

        // Assert
        (await ReadPropertiesColumnAsync(connectionFactory)).Should().BeNull();
    }

    [TestMethod]
    public async Task RestoreTheListedPropertiesOnFlushAndOnRelay()
    {
        // Arrange — the listener throws on its first call, so the flush fails and the relay redelivers.
        await using var connectionFactory = new StubConnectionFactory();
        await using var host = await StartHostAsync(connectionFactory);
        var probe = host.GetRequiredService<ListenerProbe>();
        probe.ThrowOnNextCalls(1);

        // Act
        await PublishAsync(host, connectionFactory, Properties(("a", "alpha"), ("b", DeviceId), ("c", 42)));
        MessageContext.RestoreContext(null);
        await host.GetRequiredService<IOutboxFlusher>().FlushAsync(CancellationToken.None);
        await WaitForRetryReleasedAsync(connectionFactory);
        await CreateRelay(host).ProcessPendingAsync(CancellationToken.None);

        // Assert — both attempts ran under the listed properties, with their declared types.
        probe.Properties.Should().HaveCount(2);
        probe.Properties.Should().AllSatisfy(properties =>
        {
            properties.Keys.Should().BeEquivalentTo("a", "b");
            properties["a"].Should().BeOfType<string>().Which.Should().Be("alpha");
            properties["b"].Should().BeOfType<Guid>().Which.Should().Be(DeviceId);
        });
    }

    [TestMethod]
    public async Task DeliverAnEntryWithNoPropertiesAsBefore()
    {
        // Arrange — an entry written before the host listed any properties: its column is NULL.
        await using var connectionFactory = new StubConnectionFactory();
        await using var host = await StartHostAsync(connectionFactory);
        var probe = host.GetRequiredService<ListenerProbe>();

        await connectionFactory.BeginTransactionAsync();
        await new DbConnectionOutboxStore(connectionFactory, new SqliteDialect(), Options.Create(ListedOptions())).SaveAsync(
            [
                new OutboxEntry
                {
                    MessageId = "pre-deploy",
                    ConversationId = "pre-deploy",
                    Type = typeof(AuditTrailEvent).FullName!,
                    Payload = """{"detail":"old"}""",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
            ],
            connectionFactory.GetCurrentTransaction()!,
            CancellationToken.None);
        await connectionFactory.CommitAsync();

        // Act
        await CreateRelay(host).ProcessPendingAsync(CancellationToken.None);

        // Assert
        probe.Properties.Should().ContainSingle().Which.Should().BeEmpty();
    }

    [TestMethod]
    public void RefuseAnUnsupportedPropertyType()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => options.PersistedProperties["when"] = typeof(DateTime))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain("when").And.Contain("System.DateTime");
    }

    [TestMethod]
    public void RoundTripEverySupportedType()
    {
        // Arrange
        var persisted = new Dictionary<string, Type>
        {
            ["s"] = typeof(string),
            ["i"] = typeof(int),
            ["l"] = typeof(long),
            ["b"] = typeof(bool),
            ["g"] = typeof(Guid),
        };
        var values = Properties(("s", "text"), ("i", 7), ("l", 9_000_000_000L), ("b", true), ("g", DeviceId));

        // Act
        var restored = OutboxProperties.Read(OutboxProperties.Write(values, persisted), persisted);

        // Assert
        restored.Should().NotBeNull();
        restored!["s"].Should().BeOfType<string>().Which.Should().Be("text");
        restored["i"].Should().BeOfType<int>().Which.Should().Be(7);
        restored["l"].Should().BeOfType<long>().Which.Should().Be(9_000_000_000L);
        restored["b"].Should().BeOfType<bool>().Which.Should().BeTrue();
        restored["g"].Should().BeOfType<Guid>().Which.Should().Be(DeviceId);
    }

    [TestMethod]
    public void RefuseAListedValueOfAnotherType()
    {
        // Arrange
        var persisted = new Dictionary<string, Type> { ["b"] = typeof(Guid) };

        // Act
        var act = () => OutboxProperties.Write(Properties(("b", DeviceId.ToString())), persisted);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*'b'*System.Guid*System.String*");
    }

    [TestMethod]
    public void RestoreWithoutAStoredValueThatNoLongerReadsAsItsType()
    {
        // Arrange — the host changed a key's declared type after the entry was written.
        var persisted = new Dictionary<string, Type> { ["a"] = typeof(int), ["b"] = typeof(Guid) };

        // Act
        var restored = OutboxProperties.Read($$"""{"a":"alpha","b":"{{DeviceId}}","c":1}""", persisted);

        // Assert — the unreadable and the unlisted values are dropped, the rest restored.
        restored.Should().NotBeNull();
        restored!.Keys.Should().BeEquivalentTo("b");
    }

    private static OutboxOptions ListedOptions()
    {
        var options = new OutboxOptions { AutoCreateTable = false, GracePeriod = TimeSpan.Zero };
        options.PersistedProperties["a"] = typeof(string);
        options.PersistedProperties["b"] = typeof(Guid);
        return options;
    }

    private static Dictionary<string, object?> Properties(params (string Key, object? Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value);

    private static async Task<TestWebHost> StartHostAsync(StubConnectionFactory connectionFactory)
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
                    options.PersistedProperties["a"] = typeof(string);
                    options.PersistedProperties["b"] = typeof(Guid);
                }));
            });
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            builder.Services.AddSingleton<ListenerProbe>();
            builder.Services.AddSingleton<IConduitTransport>(new SpyTransport());
        });
    }

    // Executes a command that publishes an AuditTrailEvent, inside a transaction, under a context holding the
    // given properties (as a transport would have started it).
    private static async Task PublishAsync(
        TestWebHost host, StubConnectionFactory connectionFactory, IReadOnlyDictionary<string, object?> properties)
    {
        MessageContext.StartDispatchFromTransport("trigger", null, "conversation", properties);
        await connectionFactory.BeginTransactionAsync();
        _ = await host.GetRequiredService<IMediator>().ExecuteAsync(new RecordAuditCommand("detail"), CancellationToken.None);
        await connectionFactory.CommitAsync();
    }

    private static OutboxRelay CreateRelay(TestWebHost host) => new(
        host.GetRequiredService<IServiceScopeFactory>(),
        host.GetRequiredService<TransportDispatcher>(),
        Options.Create(ListedOptions()),
        NullLogger<OutboxRelay>.Instance);

    private static async Task<string?> ReadPropertiesColumnAsync(StubConnectionFactory connectionFactory)
    {
        using var query = connectionFactory.Connection.CreateCommand();
        query.CommandText = "SELECT Properties FROM conduit_outbox";
        return await query.ExecuteScalarAsync() as string;
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
}

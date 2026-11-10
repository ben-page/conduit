using System.Text.Json;
using AwesomeAssertions;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Tests.Support;
using Conduit.Generated;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Durability.Tests.Integration;

/// <summary>
/// The inbox behind a hub registered with <see cref="ConduitSignalROptions.TrustCallerContext"/>: as over HTTP, a
/// failed dispatch releases the record its gate made, so the caller's retry under the same MessageId runs.
/// </summary>
[TestClass]
public class InboxOverSignalRShould
{
    [TestMethod]
    public async Task RunTheRetryOfAFailedTrustedDispatch()
    {
        // Arrange — a trusting hub; the handler fails its first delivery
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        var inboxStore = new DbConnectionInboxStore(connectionFactory, new SqliteDialect());
        await inboxStore.EnsureTableExistsAsync(CancellationToken.None);
        var deliveries = new DeliveryCounter();
        await using var host = StartHost(connectionFactory, deliveries);
        await using var connection = await ConnectAsync(host.Address);
        var payload = JsonSerializer.SerializeToElement(new FlakyInboxCommand(Failures: 1), ConduitRuntime.JsonOptions);
        var context = CallerContext("msg-retried");
        Dictionary<string, string>? noHeaders = null;

        // Act — the same message twice, under the same ids
        var firstAttempt = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", typeof(FlakyInboxCommand).FullName, payload, context, noHeaders);
        var retry = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", typeof(FlakyInboxCommand).FullName, payload, context, noHeaders);

        // Assert — the retry ran the handler and answered Ok
        firstAttempt.Error.Should().NotBeNull();
        firstAttempt.Error!.Code.Should().Be("InternalError");
        retry.Error.Should().BeNull();
        deliveries.Count.Should().Be(2, "the retry ran its handler rather than being discarded as a duplicate");
        (await inboxStore.ExistsAsync("msg-retried", CancellationToken.None)).Should().BeTrue();
    }

    [TestMethod]
    public async Task RunTheRetryOfAFailedTrustedEvent()
    {
        // Arrange — a trusting hub; the listener fails its first delivery
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        var inboxStore = new DbConnectionInboxStore(connectionFactory, new SqliteDialect());
        await inboxStore.EnsureTableExistsAsync(CancellationToken.None);
        var deliveries = new DeliveryCounter();
        await using var host = StartHost(connectionFactory, deliveries);
        await using var connection = await ConnectAsync(host.Address);
        var payload = JsonSerializer.SerializeToElement(new FlakyInboxEvent(Failures: 1), ConduitRuntime.JsonOptions);
        var context = CallerContext("msg-republished");
        Dictionary<string, string>? noHeaders = null;

        // Act
        var firstAttempt = async () => await connection.InvokeAsync(
            "Publish", typeof(FlakyInboxEvent).FullName, payload, context, noHeaders);
        await firstAttempt.Should().ThrowAsync<HubException>();
        await connection.InvokeAsync("Publish", typeof(FlakyInboxEvent).FullName, payload, context, noHeaders);

        // Assert
        deliveries.Count.Should().Be(2, "the retry ran its listener rather than being discarded as a duplicate");
        (await inboxStore.ExistsAsync("msg-republished", CancellationToken.None)).Should().BeTrue();
    }

    // As a C# SignalRTransport sends it: all three ids
    private static Dictionary<string, string> CallerContext(string messageId) => new()
    {
        ["MessageId"] = messageId,
        ["CausationId"] = messageId,
        ["ConversationId"] = messageId,
    };

    private static TestWebHost StartHost(StubConnectionFactory connectionFactory, DeliveryCounter deliveries)
        => TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton(deliveries);
                builder.Services.AddConduit(conduit =>
                {
                    conduit.UseSqlite("Data Source=:memory:");
                    conduit.AddDurability(durability => durability.AddInbox());
                });
                builder.Services.AddConduitSignalR(options => options.TrustCallerContext = true);

                // Override with the shared in-memory connection, so the test's store sees the host's rows
                builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
            },
            app => app.MapHub<ConduitHub>("/conduit"));

    private static async Task<HubConnection> ConnectAsync(string address)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{address}/conduit")
            .Build();
        await connection.StartAsync();
        return connection;
    }
}

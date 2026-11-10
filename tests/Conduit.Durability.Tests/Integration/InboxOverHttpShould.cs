using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Tests.Support;
using Conduit.Generated;
using Conduit.Persistence;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Durability.Tests.Integration;

/// <summary>
/// The inbox behind the HTTP wire endpoint. A trusting edge takes the caller's MessageId, so the gate records it before
/// the handler runs; a Conduit caller retries a failed dispatch under that same id (the outbox relay's next attempt, a
/// resilience retry). Were the failed attempt's record kept, the retry would be answered as an empty success and the
/// message lost.
/// </summary>
[TestClass]
public class InboxOverHttpShould
{
    [TestMethod]
    public async Task RunTheRetryOfAFailedTrustedDispatch()
    {
        // Arrange — a trusting edge; the handler fails its first delivery
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        var inboxStore = await CreateInboxStoreAsync(connectionFactory);
        var deliveries = new DeliveryCounter();
        await using var host = StartHost(connectionFactory, deliveries, trustCallerContext: true);
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };

        // Act — the same message twice, under the same ids
        using var firstAttempt = await PostAsync(client, "dispatch", new FlakyInboxCommand(Failures: 1), "msg-retried");
        using var retry = await PostAsync(client, "dispatch", new FlakyInboxCommand(Failures: 1), "msg-retried");

        // Assert — the retry ran the handler and answered Ok, and that success is what the inbox now holds
        firstAttempt.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        deliveries.Count.Should().Be(2, "the retry ran its handler rather than being discarded as a duplicate");
        (await inboxStore.ExistsAsync("msg-retried", CancellationToken.None)).Should().BeTrue();
    }

    [TestMethod]
    public async Task RunTheRetryOfAFailedTrustedEvent()
    {
        // Arrange — a trusting edge; the listener fails its first delivery
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        var inboxStore = await CreateInboxStoreAsync(connectionFactory);
        var deliveries = new DeliveryCounter();
        await using var host = StartHost(connectionFactory, deliveries, trustCallerContext: true);
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };

        // Act
        using var firstAttempt = await PostAsync(client, "publish", new FlakyInboxEvent(Failures: 1), "msg-republished");
        using var retry = await PostAsync(client, "publish", new FlakyInboxEvent(Failures: 1), "msg-republished");

        // Assert
        firstAttempt.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        deliveries.Count.Should().Be(2, "the retry ran its listener rather than being discarded as a duplicate");
        (await inboxStore.ExistsAsync("msg-republished", CancellationToken.None)).Should().BeTrue();
    }

    // A distrusting edge ignores the caller's ids and, with no enricher, starts no context: the gate sees no MessageId
    // and records nothing, as before.
    [TestMethod]
    public async Task RecordNothingBehindADistrustingEdge()
    {
        // Arrange
        var connectionFactory = new StubConnectionFactory();
        await using var _ = connectionFactory;
        await CreateInboxStoreAsync(connectionFactory);
        await using var host = StartHost(connectionFactory, new DeliveryCounter(), trustCallerContext: false);
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };

        // Act
        using var response = await PostAsync(client, "dispatch", new FlakyInboxCommand(Failures: 0), "msg-ignored");

        // Assert
        await using var count = connectionFactory.Connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM conduit_inbox";
        Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(0, "a distrusting edge's dispatch carries no MessageId for the gate to record");
    }

    private static async Task<DbConnectionInboxStore> CreateInboxStoreAsync(StubConnectionFactory connectionFactory)
    {
        var inboxStore = new DbConnectionInboxStore(connectionFactory, new SqliteDialect());
        await inboxStore.EnsureTableExistsAsync(CancellationToken.None);
        return inboxStore;
    }

    private static TestWebHost StartHost(
        StubConnectionFactory connectionFactory,
        DeliveryCounter deliveries,
        bool trustCallerContext) => TestWebHost.Start(
        builder =>
        {
            builder.Services.AddSingleton(deliveries);
            builder.Services.AddConduit(conduit =>
            {
                conduit.UseSqlite("Data Source=:memory:");
                conduit.AddDurability(durability => durability.AddInbox());
            });

            // Override with the shared in-memory connection, so the test's store sees the host's rows
            builder.Services.AddSingleton<IConduitConnectionFactory>(connectionFactory);
        },
        app => app.MapConduitEndpoint(trustCallerContext: trustCallerContext));

    // As a C# HttpTransport sends it: the type and both ids in headers, the message as the body
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object message, string messageId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conduit/{route}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(message, message.GetType(), ConduitRuntime.JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Conduit-Type", message.GetType().FullName);
        request.Headers.Add("X-Conduit-Message-Id", messageId);
        request.Headers.Add("X-Conduit-Conversation-Id", messageId);
        return await client.SendAsync(request);
    }
}

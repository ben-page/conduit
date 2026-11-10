using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Generated;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class ConduitHubShould
{
    private static TestWebHost CreateHost()
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    private static async Task<HubConnection> ConnectToHub(string address)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{address}/conduit")
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [TestMethod]
    public async Task ReturnOkResultForCommand()
    {
        // Arrange
        await using var host = CreateHost();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Name = "Hub Test" });

        // Act
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.CreateItemCommand", payload, noContext, noHeaders);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
    }

    [TestMethod]
    public async Task ReturnFailForConduitException()
    {
        // Arrange
        await using var host = CreateHost();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });

        // Act
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.GetItemQuery", payload, noContext, noHeaders);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().NotBeNull();
        result.Error.Code.Should().Be("NotFound");
    }

    [TestMethod]
    public async Task DeliverPublishedEventToListener()
    {
        // Arrange
        await using var host = CreateHost();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(
            new { Id = Guid.NewGuid(), Name = "Hub Event" });

        // Act
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        await connection.InvokeAsync(
            "Publish", "Conduit.Transport.SignalR.Tests.Support.ItemCreatedEvent", payload, noContext, noHeaders);

        // Assert
        await spy.WaitForCountAsync(1);
    }

    [TestMethod]
    public async Task PropagateListenerFailureAsHubError()
    {
        // Arrange — ExplodingEventListener throws InvalidOperationException.
        // The wire event dispatch now propagates EventListenerException so
        // transports (service bus, SignalR) can decide how to handle the failure.
        await using var host = CreateHost();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Data = "boom" });

        // Act — publish an event whose listener throws
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        var act = () => connection.InvokeAsync(
            "Publish",
            "Conduit.Transport.SignalR.Tests.Support.ExplodingEvent",
            payload,
            noContext,
            noHeaders);

        // Assert — exception propagates to the caller
        await act.Should().ThrowAsync<Exception>();
    }

    // A behavior that throws fails the event outside its listeners: Publish answers nothing, so the invocation fails
    // with the fixed message an unexpected failure is answered with, and nothing of the exception itself
    [TestMethod]
    public async Task FailAPublishWhoseBehaviorThrowsWithTheFixedMessage()
    {
        // Arrange
        await using var host = TestWebHost.Start(
            builder =>
            {
                builder.Services.AddConduit(conduit => conduit.AddPipeline(typeof(ThrowingEventBehavior<,>)));
                builder.Services.AddConduitSignalR();
            },
            app => app.MapHub<ConduitHub>("/conduit"));
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Data = "x" });
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;

        // Act
        var publish = () => connection.InvokeAsync(
            "Publish", typeof(BehaviorFailureEvent).FullName, payload, noContext, noHeaders);

        // Assert -- a HubException (the server's own message reaches the client only through one) with the fixed text
        var failure = await publish.Should().ThrowExactlyAsync<HubException>();
        failure.Which.Message.Should().EndWith("HubException: An unexpected error occurred.");
        failure.Which.Message.Should().NotContain("The behavior failed.");
    }

    private static TestWebHost CreateHost(ConduitServerInfo serverInfo)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR(serverInfo);
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    // Sent on connect rather than broadcast on restart: a restart broadcast reaches the clients connected to that pod,
    // which at startup is nobody, and events do not replay. Every client that needs the id necessarily connects.
    [TestMethod]
    public async Task SendTheRegisteredServerInfoToACallerOnConnect()
    {
        // Arrange
        await using var host = CreateHost(new ConduitServerInfo("build-123"));
        var received = new TaskCompletionSource<ConduitServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{host.Address}/conduit")
            .Build();
        connection.On<ConduitServerInfo>(ConduitHub.ServerInfoMethod, info => received.TrySetResult(info));

        // Act
        await connection.StartAsync();

        // Assert
        var info = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        info.Should().Be(new ConduitServerInfo("build-123"));
    }

    // Carried, never derived: a host that registers no ConduitServerInfo sends nothing.
    [TestMethod]
    public async Task SendNoServerInfoWhenTheHostRegistersNone()
    {
        // Arrange
        await using var host = CreateHost();
        var received = new TaskCompletionSource<ConduitServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{host.Address}/conduit")
            .Build();
        connection.On<ConduitServerInfo>(ConduitHub.ServerInfoMethod, info => received.TrySetResult(info));
        await connection.StartAsync();
        var payload = JsonSerializer.SerializeToElement(new { Name = "After connect" });

        // Act: a round trip on the same connection, then a bounded wait. The server reads nothing from a connection
        // until its OnConnectedAsync has returned, so anything it sent arrived before the round trip's result — but the
        // client runs On handlers on a queue separate from invocation completions, so the round trip alone does not
        // prove a handler would have run by now. The wait gives one the time to.
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.CreateItemCommand", payload, noContext, noHeaders);
        var first = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));

        // Assert
        first.Should().NotBeSameAs(received.Task);
        received.Task.IsCompleted.Should().BeFalse();
    }

    private const string CaptureContextType = "Conduit.Transport.SignalR.Tests.Support.CaptureContextCommand";

    private static readonly Dictionary<string, string> CallerContext = new()
    {
        ["MessageId"] = "m-1",
        ["ConversationId"] = "c-1",
        ["CausationId"] = "p-1",
    };

    private static TestWebHost CreateContextHost(Action<ConduitSignalROptions>? configure)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<CapturedContexts>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR(configure);
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    private static async Task DispatchCaptureContextAsync(HubConnection connection, Dictionary<string, string> context)
    {
        var payload = JsonSerializer.SerializeToElement(new { });
        Dictionary<string, string>? noHeaders = null;
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", CaptureContextType, payload, context, noHeaders);
        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
    }

    // The hub faces browsers by default: the inbox dedups on MessageId and audit lineage follows ConversationId, so a
    // context a caller passes is honored only by a hub registered with TrustCallerContext (a service-to-service edge).
    [TestMethod]
    public async Task IgnoreCallerContextUnlessConfigured()
    {
        // Arrange
        await using var host = CreateContextHost(configure: null);
        var captured = host.GetRequiredService<CapturedContexts>();
        await using var connection = await ConnectToHub(host.Address);

        // Act
        await DispatchCaptureContextAsync(connection, CallerContext);

        // Assert
        var ids = captured.Ids.Should().ContainSingle().Subject;
        ids.MessageId.Should().NotBe("m-1");
        ids.ConversationId.Should().Be(ids.MessageId, "a minted dispatch is the head of its own conversation");
        ids.CausationId.Should().Be(ids.MessageId);
    }

    [TestMethod]
    public async Task HonorCallerContextWhenConfigured()
    {
        // Arrange
        await using var host = CreateContextHost(options => options.TrustCallerContext = true);
        var captured = host.GetRequiredService<CapturedContexts>();
        await using var connection = await ConnectToHub(host.Address);

        // Act
        await DispatchCaptureContextAsync(connection, CallerContext);

        // Assert
        captured.Ids.Should().ContainSingle().Which.Should().Be(new MessageContextSnapshot("m-1", "p-1", "c-1"));
    }

    [TestMethod]
    public async Task MintFreshIdsForACallerContextThatIsNotPlainTokensEvenWhenConfigured()
    {
        // Arrange: JSON carries what an HTTP header cannot, so CR/LF reaches the hub intact.
        await using var host = CreateContextHost(options => options.TrustCallerContext = true);
        var captured = host.GetRequiredService<CapturedContexts>();
        await using var connection = await ConnectToHub(host.Address);
        Dictionary<string, string>[] contexts =
        [
            new(CallerContext) { ["MessageId"] = "m-1\r\nforged log line" },
            new(CallerContext) { ["ConversationId"] = new string('c', 65) },
            new(CallerContext) { ["CausationId"] = "p-\u00e9" },
        ];
        var supplied = contexts.SelectMany(context => context.Values).ToHashSet();

        // Act
        foreach (var context in contexts)
        {
            await DispatchCaptureContextAsync(connection, context);
        }

        // Assert
        captured.Ids.Should().HaveCount(contexts.Length);
        foreach (var ids in captured.Ids)
        {
            supplied.Should().NotContain(ids.MessageId!);
            ids.ConversationId.Should().Be(ids.MessageId, "one bad id discards the caller's context whole");
            ids.CausationId.Should().Be(ids.MessageId);
        }
    }
}

using System.Text.Json;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.SignalR.Tests.Integration;

/// <summary>
/// The hub tells every <see cref="IInboundFailureObserver"/> of an unexpected failure before it answers (the inbox
/// releases its record there), and of nothing else.
/// </summary>
[TestClass]
public class InboundFailureObserverShould
{
    // The hub's optional context and headers, absent as a browser leaves them (SignalR needs every argument sent)
    private static Dictionary<string, string>? NoContext => null;

    private static Dictionary<string, string>? NoHeaders => null;

    [TestMethod]
    public async Task HearOfARequestThatAnsweredInternalErrorUnderItsReference()
    {
        // Arrange
        var observer = new RecordingFailureObserver();
        await using var host = StartHost(observer);
        await using var connection = await ConnectAsync(host.Address);

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", typeof(ExplodingCommand).FullName, JsonSerializer.SerializeToElement(new { }), NoContext, NoHeaders);

        // Assert
        result.Error!.Code.Should().Be("InternalError");
        var failure = observer.Failures.Should().ContainSingle().Subject;
        failure.MessageType.Should().Be(typeof(ExplodingCommand).FullName);
        failure.Error.Reference.Should().Be(result.Error.Reference);
    }

    // A refusal is an answer, and a retry would be refused again: nothing to undo
    [TestMethod]
    public async Task NotHearOfARefusal()
    {
        // Arrange
        var observer = new RecordingFailureObserver();
        await using var host = StartHost(observer);
        await using var connection = await ConnectAsync(host.Address);

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "No.Such.Command", JsonSerializer.SerializeToElement(new { }), NoContext, NoHeaders);

        // Assert
        result.Error!.Code.Should().Be("BadRequest");
        observer.Failures.Should().BeEmpty();
    }

    [TestMethod]
    public async Task HearOfAnEventWhoseListenerFailed()
    {
        // Arrange
        var observer = new RecordingFailureObserver();
        await using var host = StartHost(observer);
        await using var connection = await ConnectAsync(host.Address);

        // Act
        var publish = async () => await connection.InvokeAsync(
            "Publish", typeof(ExplodingEvent).FullName, JsonSerializer.SerializeToElement(new { Data = "x" }), NoContext, NoHeaders);

        // Assert
        await publish.Should().ThrowAsync<HubException>();
        observer.Failures.Should().ContainSingle()
            .Which.MessageType.Should().Be(typeof(ExplodingEvent).FullName);
    }

    [TestMethod]
    public async Task LogAThrowingObserverAndStillTellTheOthers()
    {
        // Arrange — the throwing observer registered first
        var spy = new SpyLoggerProvider();
        var observer = new RecordingFailureObserver();
        await using var host = TestWebHost.Start(
            builder =>
            {
                builder.Logging.AddProvider(spy);
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                builder.Services.AddSingleton<IInboundFailureObserver, ThrowingFailureObserver>();
                builder.Services.AddSingleton<IInboundFailureObserver>(observer);
            },
            app => app.MapHub<ConduitHub>("/conduit"));
        await using var connection = await ConnectAsync(host.Address);

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", typeof(ExplodingCommand).FullName, JsonSerializer.SerializeToElement(new { }), NoContext, NoHeaders);

        // Assert — the answer unchanged, the other observer told, the throw logged at Warning with its exception
        result.Error!.Code.Should().Be("InternalError");
        observer.Failures.Should().ContainSingle();
        var logged = spy.Entries.Should().ContainSingle(entry => entry.EventId.Id == 101).Subject;
        logged.EventId.Name.Should().Be("FailureObserverFailed");
        logged.Level.Should().Be(LogLevel.Warning);
        logged.Message.Should().Contain(nameof(ThrowingFailureObserver));
        logged.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private static TestWebHost StartHost(RecordingFailureObserver observer) => TestWebHost.Start(
        builder =>
        {
            builder.Services.AddConduit();
            builder.Services.AddConduitSignalR();
            builder.Services.AddSingleton<IInboundFailureObserver>(observer);
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

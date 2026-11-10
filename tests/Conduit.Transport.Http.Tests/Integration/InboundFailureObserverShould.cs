using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.Http.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.Http.Tests.Integration;

/// <summary>
/// The wire endpoint tells every <see cref="IInboundFailureObserver"/> of an unexpected failure before it answers (the
/// inbox releases its record there), and of nothing else.
/// </summary>
[TestClass]
public class InboundFailureObserverShould
{
    [TestMethod]
    public async Task HearOfARequestThatAnsweredInternalErrorUnderItsReference()
    {
        // Arrange
        var observer = new RecordingFailureObserver();
        await using var host = StartHost(observer);
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };

        // Act
        using var response = await PostAsync(client, "dispatch", typeof(ThrowGenericCommand).FullName!, new { });

        // Assert
        ((int)response.StatusCode).Should().Be(500);
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("reference").GetString();
        var failure = observer.Failures.Should().ContainSingle().Subject;
        failure.MessageType.Should().Be(typeof(ThrowGenericCommand).FullName);
        failure.Error.Code.Should().Be("InternalError");
        failure.Error.Reference.Should().Be(reference);
    }

    // A refusal is an answer, and a retry would be refused again: nothing to undo
    [TestMethod]
    public async Task NotHearOfARefusal()
    {
        // Arrange
        var observer = new RecordingFailureObserver();
        await using var host = StartHost(observer);
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };

        // Act
        using var response = await PostAsync(client, "dispatch", typeof(ThrowConflictCommand).FullName!, new { });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        observer.Failures.Should().BeEmpty();
    }

    [TestMethod]
    public async Task HearOfAnEventWhoseListenerFailed()
    {
        // Arrange
        var observer = new RecordingFailureObserver();
        await using var host = StartHost(observer);
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };

        // Act
        using var response = await PostAsync(
            client, "publish", typeof(ListenerFailureEvent).FullName!, new { Data = "x" });

        // Assert
        ((int)response.StatusCode).Should().Be(500);
        observer.Failures.Should().ContainSingle()
            .Which.MessageType.Should().Be(typeof(ListenerFailureEvent).FullName);
    }

    [TestMethod]
    public async Task HearOfAnEventWhoseDispatchFailedOutsideItsListeners()
    {
        // Arrange
        var observer = new RecordingFailureObserver();
        await using var host = StartHost(observer, conduit => conduit.AddPipeline(typeof(ThrowingEventBehavior<,>)));
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };

        // Act
        using var response = await PostAsync(
            client, "publish", typeof(BehaviorFailureEvent).FullName!, new { Data = "x" });

        // Assert
        ((int)response.StatusCode).Should().Be(500);
        observer.Failures.Should().ContainSingle()
            .Which.MessageType.Should().Be(typeof(BehaviorFailureEvent).FullName);
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
                builder.Services.AddSingleton<IInboundFailureObserver, ThrowingFailureObserver>();
                builder.Services.AddSingleton<IInboundFailureObserver>(observer);
            },
            app => app.MapConduitEndpoint());
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };

        // Act
        using var response = await PostAsync(client, "dispatch", typeof(ThrowGenericCommand).FullName!, new { });

        // Assert — the answer unchanged, the other observer told, the throw logged at Warning with its exception
        ((int)response.StatusCode).Should().Be(500);
        observer.Failures.Should().ContainSingle();
        var logged = spy.Entries.Should().ContainSingle(entry => entry.EventId.Id == 100).Subject;
        logged.EventId.Name.Should().Be("FailureObserverFailed");
        logged.Level.Should().Be(LogLevel.Warning);
        logged.Message.Should().Contain(nameof(ThrowingFailureObserver));
        logged.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private static TestWebHost StartHost(
        RecordingFailureObserver observer,
        Action<ConduitBuilder>? configure = null) => TestWebHost.Start(
        builder =>
        {
            builder.Services.AddConduit(conduit => configure?.Invoke(conduit));
            builder.Services.AddSingleton<IInboundFailureObserver>(observer);
        },
        app => app.MapConduitEndpoint());

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, string type, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conduit/{route}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ConduitRuntime.JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Conduit-Type", type);
        return await client.SendAsync(request);
    }
}

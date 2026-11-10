using System.Text.Json;
using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Runtime;
using Conduit.Transport.Http;
using Conduit.TestUtils;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.Http.Tests.Integration;

[TestClass]
public class HttpTransportShould
{
    private static TestWebHost CreateHandlerHost()
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddConduit();
            },
            app => app.MapConduitEndpoint());
    }

    private static TestWebHost CreateCallerHost(string remoteAddress)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                    {
                        endpoint.UseHttp(
                            new HttpClient { BaseAddress = new Uri(remoteAddress) });
                        endpoint.RouteNamespace<CreateItemCommand>();
                    });
                });
            });
    }

    [TestMethod]
    public async Task RoundTripCommandWithResult()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("HTTP Test"));

        // Assert
        id.Should().NotBeEmpty();
    }

    // A routed [FireAndForget] command goes out with SendEventAsync: over HTTP that is a POST to /dispatch whose reply
    // is discarded. The receiving endpoint runs the handler inline, so it has run by the time the send returns.
    [TestMethod]
    public async Task SendAFireAndForgetCommandWithoutAReply()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        var store = handlerHost.GetRequiredService<ItemStore>();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();
        var id = Guid.NewGuid();

        // Act
        await mediator.ExecuteAsync(new NoteItemCommand(id, "Noted"));

        // Assert
        store.Get(id).Should().Be("Noted");
        callerHost.GetRequiredService<ItemStore>().Get(id).Should().BeNull();
    }

    [TestMethod]
    public async Task RoundTripVoidCommand()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        var store = handlerHost.GetRequiredService<ItemStore>();
        var id = Guid.NewGuid();
        store.Add(id, "To Delete");
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new DeleteItemCommand(id));

        // Assert
        store.Get(id).Should().BeNull();
    }

    [TestMethod]
    public async Task RoundTripQuery()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        handlerHost.GetRequiredService<ItemStore>().Add(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "Query Test");

        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.FetchAsync(new GetItemQuery(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")));

        // Assert
        result.Should().Be("Query Test");
    }

    [TestMethod]
    public async Task ReachTheConfiguredBasePathWhenACallerSuppliedHttpClientOmitsIt()
    {
        // Arrange — handler host maps its dispatch endpoint at a non-default base path
        await using var handlerHost = TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddConduit();
            },
            app => app.MapConduitEndpoint("/api/messages"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Conduit:Endpoints:remote:BasePath"] = "/api/messages",
            })
            .Build();

        // Caller supplies its own HttpClient (no basePath argument) — the configured BasePath
        // must be the one that reaches HttpTransport, not the "/api/conduit" default.
        await using var callerHost = TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddConduit(configuration, conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                    {
                        endpoint.UseHttp(new HttpClient { BaseAddress = new Uri(handlerHost.Address) });
                        endpoint.RouteNamespace<CreateItemCommand>();
                    });
                });
            });
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("Configured Base Path"));

        // Assert
        id.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task PropagateNotFoundAsStructuredError()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.FetchAsync(new GetItemQuery(Guid.NewGuid()));

        // Assert — HTTP transport now reads structured DispatchResult errors, consistent with other transports
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("*NotFound*");
    }

    // /publish answers a listener's failure with the InternalError envelope, and the transport reads the envelope
    // before the status: a remote InternalError (what Resilience's TransientErrors retries), with the reference the
    // remote host logged, never an HttpRequestException
    [TestMethod]
    public async Task ReadAnEventWhoseRemoteListenerFailedAsARemoteInternalError()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        using var httpClient = new HttpClient { BaseAddress = new Uri(handlerHost.Address) };
        var transport = new HttpTransport(
            httpClient, typeRegistry: handlerHost.GetRequiredService<ConduitTypeRegistry>());
        var envelope = new DispatchEnvelope(
            typeof(ListenerFailureEvent).FullName!,
            JsonSerializer.SerializeToElement(new { Data = "x" }, ConduitRuntime.JsonOptions));

        // Act
        var action = () => transport.SendEventAsync(envelope, CancellationToken.None);

        // Assert
        var failure = (await action.Should().ThrowExactlyAsync<RemoteDispatchException>()).Which;
        failure.Code.Should().Be(ErrorCode.InternalError);
        failure.Reference.Should().NotBeNullOrEmpty();
        failure.Message.Should().NotContain("The listener failed.");
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.Http.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Http.Tests.Integration;

/// <summary>
/// Pins who owns a dispatch's correlation ids at the HTTP edges. The inbox dedups on <c>MessageContext.MessageId</c>
/// and audit lineage follows <c>ConversationId</c>/<c>CausationId</c>, so an id a caller supplies is honored only on
/// a wire endpoint mapped with <c>trustCallerContext: true</c> (a service-to-service edge), and only when every
/// supplied id is a plain token. Everywhere else the server mints them, and the request still succeeds.
/// </summary>
[TestClass]
public class HttpDispatchContextShould
{
    private const string CaptureType = "Conduit.Transport.Http.Tests.Support.CaptureContextCommand";
    private const string CaptureEventType = "Conduit.Transport.Http.Tests.Support.CaptureContextEvent";
    private const string DispatchPath = "/api/conduit/dispatch";
    private const string PublishPath = "/api/conduit/publish";

    private static readonly Dictionary<string, string> CallerIds = new()
    {
        ["X-Conduit-Message-Id"] = "m-1",
        ["X-Conduit-Conversation-Id"] = "c-1",
        ["X-Conduit-Causation-Id"] = "p-1",
    };

    // What the TS HttpTransport sends for `headers` { TenantCode, culture, UserId }, the prefix in two letter cases.
    private static readonly Dictionary<string, string> ClientProperties = new()
    {
        ["X-Conduit-Property-TenantCode"] = "t1",
        ["x-conduit-property-culture"] = "es",
        ["X-Conduit-Property-UserId"] = "spoofed",
    };

    private static (WebApplication App, HttpClient Client, CapturedContexts Captured, MessageIdDedupGate Gate) CreateTestHost(
        bool trustCallerContext,
        IDispatchContextEnricher? enricher = null,
        Action<WebApplication>? mapMore = null,
        bool? readClientProperties = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<CapturedContexts>();
        var gate = new MessageIdDedupGate();
        builder.Services.AddSingleton<IInboundInterceptor>(gate);
        builder.Services.AddConduit();
        if (enricher is not null)
        {
            builder.Services.AddSingleton(enricher);
        }

        var app = builder.Build();
        // Null maps the endpoint without the argument, so a test can pin MapConduitEndpoint's own default.
        if (readClientProperties is { } read)
        {
            app.MapConduitEndpoint(trustCallerContext: trustCallerContext, readClientProperties: read);
        }
        else
        {
            app.MapConduitEndpoint(trustCallerContext: trustCallerContext);
        }

        mapMore?.Invoke(app);
        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client, app.Services.GetRequiredService<CapturedContexts>(), gate);
    }

    private static async Task<HttpResponseMessage> PostCaptureAsync(
        HttpClient client, string path, IReadOnlyDictionary<string, string> headers, string? type = CaptureType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        if (type is not null)
        {
            request.Headers.Add("X-Conduit-Type", type);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return await client.SendAsync(request);
    }

    // A header value no HTTP/1.1 parser delivers (CR/LF), or one a host's header encoding may (non-ASCII), is handed to
    // the mapped /dispatch endpoint verbatim: its request delegate runs in process over a DefaultHttpContext, as
    // Kestrel would run it, minus the socket.
    private static async Task<(int Status, JsonElement Body)> InvokeDispatchInProcessAsync(
        WebApplication app, IReadOnlyDictionary<string, string> headers)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == DispatchPath);

        await using var scope = app.Services.CreateAsyncScope();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = DispatchPath;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Body = new MemoryStream("{}"u8.ToArray());
        httpContext.Request.Headers["X-Conduit-Type"] = CaptureType;
        foreach (var (name, value) in headers)
        {
            httpContext.Request.Headers[name] = value;
        }

        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        await endpoint.RequestDelegate!(httpContext);

        responseBody.Position = 0;
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(responseBody);
        return (httpContext.Response.StatusCode, body);
    }

    [TestMethod]
    public async Task MintFreshIdsWhenTheEdgeDoesNotTrustCallers()
    {
        // Arrange: a browser on a plain /dispatch sends the same ids twice, as it would to suppress the second dispatch.
        var (app, client, captured, gate) = CreateTestHost(trustCallerContext: false);
        await using (app)
        using (client)
        {
            // Act
            using var first = await PostCaptureAsync(client, DispatchPath, CallerIds);
            using var second = await PostCaptureAsync(client, DispatchPath, CallerIds);

            // Assert
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            second.StatusCode.Should().Be(HttpStatusCode.OK);
            captured.Ids.Should().HaveCount(2, "a dedup keyed on MessageId dedups nothing the server did not mint");
            foreach (var ids in captured.Ids)
            {
                ids.MessageId.Should().NotBe("m-1");
                ids.ConversationId.Should().Be(ids.MessageId, "a minted dispatch is the head of its own conversation");
                ids.CausationId.Should().Be(ids.MessageId);
            }

            captured.Ids[0].MessageId.Should().NotBe(captured.Ids[1].MessageId);
            gate.Seen.Should().NotContain(["m-1", "c-1", "p-1"]);
        }
    }

    [TestMethod]
    public async Task HonorValidIdsWhenTrusted()
    {
        // Arrange
        var (app, client, captured, gate) = CreateTestHost(
            trustCallerContext: true, enricher: new StaticValueEnricher("UserId", "emp-42"));
        await using (app)
        using (client)
        {
            // Act
            using var response = await PostCaptureAsync(client, DispatchPath, CallerIds);
            using var replay = await PostCaptureAsync(client, DispatchPath, CallerIds);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            captured.Ids.Should().ContainSingle("a trusted caller's redelivery of one MessageId is deduplicated")
                .Which.Should().Be(new MessageContextSnapshot("m-1", "p-1", "c-1"));
            captured.Properties.Single()!["UserId"].Should().Be("emp-42", "server-side enrichment still applies");
            gate.Seen.Should().Equal("m-1");
        }
    }

    [TestMethod]
    public async Task RejectIdsThatAreNotPlainTokens()
    {
        // Arrange: a trusting edge, and caller ids that are each well-formed but one.
        var (app, client, captured, gate) = CreateTestHost(trustCallerContext: true);
        Dictionary<string, string>[] cases =
        [
            new(CallerIds) { ["X-Conduit-Message-Id"] = "m-1\r\nX-Injected: 1" },
            new(CallerIds) { ["X-Conduit-Conversation-Id"] = "c-1\n" },
            new(CallerIds) { ["X-Conduit-Message-Id"] = new string('m', 65) },
            new(CallerIds) { ["X-Conduit-Conversation-Id"] = new string('c', 65) },
            new(CallerIds) { ["X-Conduit-Causation-Id"] = "café" },
            new(CallerIds) { ["X-Conduit-Message-Id"] = "m­1" },
        ];
        var supplied = cases.SelectMany(headers => headers.Values).ToHashSet();
        await using (app)
        using (client)
        {
            // Act
            var answers = new List<(int Status, JsonElement Body)>();
            foreach (var headers in cases)
            {
                answers.Add(await InvokeDispatchInProcessAsync(app, headers));
            }

            // Assert
            using (new AssertionScope())
            {
                foreach (var (status, body) in answers)
                {
                    status.Should().Be(StatusCodes.Status200OK);
                    body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.String);
                }

                captured.Ids.Should().HaveCount(cases.Length);
                foreach (var ids in captured.Ids)
                {
                    supplied.Should().NotContain(ids.MessageId!);
                    ids.ConversationId.Should().Be(ids.MessageId, "one bad id discards the caller's context whole");
                    ids.CausationId.Should().Be(ids.MessageId);
                }

                gate.Seen.Should().NotIntersectWith(supplied);
            }
        }
    }

    [TestMethod]
    public async Task IgnoreCallerIdsOnARestEndpoint()
    {
        // Arrange: REST endpoints face browsers and third parties, so they never trust caller ids, even on a host whose
        // wire endpoint does.
        var (app, client, captured, _) = CreateTestHost(
            trustCallerContext: true, mapMore: host => host.MapConduitPost<CaptureContextCommand>("/api/capture"));
        await using (app)
        using (client)
        {
            // Act
            using var response = await PostCaptureAsync(client, "/api/capture", CallerIds, type: null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var ids = captured.Ids.Should().ContainSingle().Subject;
            ids.MessageId.Should().NotBe("m-1");
            ids.CausationId.Should().NotBe("m-1").And.NotBe("p-1");
            ids.ConversationId.Should().NotBe("c-1");
        }
    }

    [TestMethod]
    public async Task IgnoreCallerIdsOnAnOptedInMinimalApiRoute()
    {
        // Arrange
        var (app, client, captured, _) = CreateTestHost(
            trustCallerContext: true,
            enricher: new StaticValueEnricher("UserId", "emp-42"),
            mapMore: host => host.MapPost("/api/minimal", async (IMediator mediator) =>
            {
                await mediator.ExecuteAsync(new CaptureContextCommand());
                return Results.Ok();
            }).WithConduitDispatchContext());
        await using (app)
        using (client)
        {
            // Act
            using var response = await PostCaptureAsync(client, "/api/minimal", CallerIds, type: null);

            // Assert: the route's context is minted, so its in-process dispatch is caused by that, not by the caller.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var ids = captured.Ids.Should().ContainSingle().Subject;
            ids.CausationId.Should().NotBe("m-1");
            ids.ConversationId.Should().NotBe("c-1");
            ids.ConversationId.Should().Be(ids.CausationId, "the route's minted context heads the conversation");
            captured.Properties.Single()!["UserId"].Should().Be("emp-42");
        }
    }

    [TestMethod]
    public async Task ReadClientPropertiesFromHeaders()
    {
        // Arrange: the browser's properties (the hub's `headers` argument) travel over HTTP as X-Conduit-Property-<name>,
        // to an endpoint mapped to read them.
        var (app, client, captured, _) = CreateTestHost(
            trustCallerContext: false, enricher: new StaticValueEnricher("UserId", "emp-42"), readClientProperties: true);
        await using (app)
        using (client)
        {
            // Act
            using var dispatched = await PostCaptureAsync(client, DispatchPath, ClientProperties);
            using var published = await PostCaptureAsync(client, PublishPath, ClientProperties, CaptureEventType);

            // Assert: the prefix matches in any letter case, the name after it is the property's, and an enricher's write
            // wins over a client's property of the same name, as on the hub.
            dispatched.StatusCode.Should().Be(HttpStatusCode.OK);
            published.StatusCode.Should().Be(HttpStatusCode.OK);
            captured.Properties.Should().HaveCount(2, "one request and one event were dispatched");
            foreach (var properties in captured.Properties)
            {
                properties.Should().BeEquivalentTo(new Dictionary<string, object?>
                {
                    ["TenantCode"] = "t1",
                    ["culture"] = "es",
                    ["UserId"] = "emp-42",
                });
            }
        }
    }

    [TestMethod]
    public async Task IgnoreClientPropertiesUnlessTheEndpointReadsThem()
    {
        // Arrange: an HTTP edge fails closed. One host maps the wire endpoint by default, the other opts in.
        var (defaultApp, defaultClient, defaultCaptured, _) = CreateTestHost(trustCallerContext: false);
        var (readingApp, readingClient, readingCaptured, _) = CreateTestHost(
            trustCallerContext: false, readClientProperties: true);
        await using (defaultApp)
        await using (readingApp)
        using (defaultClient)
        using (readingClient)
        {
            // Act
            using var ignored = await PostCaptureAsync(defaultClient, DispatchPath, ClientProperties);
            using var read = await PostCaptureAsync(readingClient, DispatchPath, ClientProperties);

            // Assert
            ignored.StatusCode.Should().Be(HttpStatusCode.OK);
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            (defaultCaptured.Properties.Single() ?? new Dictionary<string, object?>()).Should().NotContainKey("TenantCode");
            readingCaptured.Properties.Single()!["TenantCode"].Should().Be("t1");
        }
    }

    [TestMethod]
    public async Task ReadAPropertyNameRegardlessOfCase()
    {
        // Arrange: HTTP/2 and HTTP/3 lower-case every header name, so a browser's TenantCode arrives as tenantcode.
        var (app, client, captured, _) = CreateTestHost(trustCallerContext: false, readClientProperties: true);
        var lowerCased = new Dictionary<string, string> { ["x-conduit-property-tenantcode"] = "t1" };
        await using (app)
        using (client)
        {
            // Act
            using var response = await PostCaptureAsync(client, DispatchPath, lowerCased);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var properties = captured.Properties.Single()!;
            properties["TenantCode"].Should().Be("t1");
            properties["TENANTCODE"].Should().Be("t1");
        }
    }

    [TestMethod]
    public async Task KeepAPropertyNamedLikeACorrelationIdOutOfTheIds()
    {
        // Arrange: a trusting edge that reads properties, and properties named like the correlation headers.
        var (app, client, captured, gate) = CreateTestHost(trustCallerContext: true, readClientProperties: true);
        var headers = new Dictionary<string, string>
        {
            ["X-Conduit-Property-X-Conduit-Message-Id"] = "m-1",
            ["X-Conduit-Property-X-Conduit-Conversation-Id"] = "c-1",
        };
        await using (app)
        using (client)
        {
            // Act
            using var response = await PostCaptureAsync(client, DispatchPath, headers);

            // Assert: they are properties and nothing else; the ids are minted.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var ids = captured.Ids.Should().ContainSingle().Subject;
            ids.MessageId.Should().NotBe("m-1");
            ids.ConversationId.Should().Be(ids.MessageId, "a minted dispatch is the head of its own conversation");
            ids.CausationId.Should().Be(ids.MessageId);
            gate.Seen.Should().NotContain("m-1");
            var properties = captured.Properties.Single()!;
            properties["X-Conduit-Message-Id"].Should().Be("m-1");
            properties["X-Conduit-Conversation-Id"].Should().Be("c-1");
        }
    }

    [TestMethod]
    public async Task DropAPropertyAnEnricherOwnsFromAnAnonymousRequest()
    {
        // Arrange: an enricher that owns UserId removes it when there is no identity, so an anonymous caller cannot
        // dispatch as someone else, whatever spelling it sends.
        var (app, client, captured, _) = CreateTestHost(
            trustCallerContext: false, enricher: new SubClaimUserIdEnricher(), readClientProperties: true);
        var spoofed = new Dictionary<string, string>
        {
            ["X-Conduit-Property-userid"] = "victim",
            ["X-Conduit-Property-TenantCode"] = "t1",
        };
        await using (app)
        using (client)
        {
            // Act
            using var response = await PostCaptureAsync(client, DispatchPath, spoofed);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var properties = captured.Properties.Single()!;
            properties.Keys.Where(key => string.Equals(key, "UserId", StringComparison.OrdinalIgnoreCase))
                .Should().BeEmpty("no spelling of the owned key survives");
            properties["TenantCode"].Should().Be("t1");
        }
    }

    [TestMethod]
    public async Task ReadNoClientPropertiesOnARestEndpointOrAnOptedInMinimalApiRoute()
    {
        // Arrange: client properties are the wire endpoint's, as the hub's `headers` are the hub's, even on a host whose
        // wire endpoint reads them.
        var (app, client, captured, _) = CreateTestHost(
            trustCallerContext: false,
            mapMore: host =>
            {
                host.MapConduitPost<CaptureContextCommand>("/api/capture");
                host.MapPost("/api/minimal", async (IMediator mediator) =>
                {
                    await mediator.ExecuteAsync(new CaptureContextCommand());
                    return Results.Ok();
                }).WithConduitDispatchContext();
            },
            readClientProperties: true);
        await using (app)
        using (client)
        {
            // Act
            using var rest = await PostCaptureAsync(client, "/api/capture", ClientProperties, type: null);
            using var minimal = await PostCaptureAsync(client, "/api/minimal", ClientProperties, type: null);

            // Assert
            rest.StatusCode.Should().Be(HttpStatusCode.OK);
            minimal.StatusCode.Should().Be(HttpStatusCode.OK);
            captured.Properties.Should().HaveCount(2);
            foreach (var properties in captured.Properties)
            {
                (properties ?? new Dictionary<string, object?>()).Should().BeEmpty();
            }
        }
    }
}

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
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.Http.Tests.Integration;

/// <summary>The HTTP wire endpoint and a REST route reach the handler through <c>ConduitDispatch</c>, so a retried command is retried.</summary>
[TestClass]
public class DispatchRetryOverHttpShould
{
    [TestMethod]
    public async Task Retry_a_command_posted_to_the_wire_endpoint()
    {
        // Arrange
        await using var host = StartHost(app => app.MapConduitEndpoint());
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };
        var probe = host.GetRequiredService<RetryProbe>();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/conduit/dispatch")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new FlakyCommand(Failures: 1), ConduitRuntime.JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Conduit-Type", typeof(FlakyCommand).FullName);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertTwoFreshAttempts(probe);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetGuid().Should().Be(probe.Sightings[1].ScopeId);
    }

    [TestMethod]
    public async Task Retry_a_command_posted_to_a_rest_route()
    {
        // Arrange
        await using var host = StartHost(app => app.MapConduitPost<FlakyCommand>("/api/flaky"));
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        var response = await client.PostAsJsonAsync("/api/flaky", new { Failures = 1 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertTwoFreshAttempts(probe);
        (await response.Content.ReadFromJsonAsync<Guid>()).Should().Be(probe.Sightings[1].ScopeId);
    }

    // Neither attempt ran in the request's own scope: each is an attempt (it has a number) in a scope of its own.
    private static void AssertTwoFreshAttempts(RetryProbe probe)
    {
        probe.Sightings.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        probe.Sightings.Select(sighting => sighting.ScopeId).Distinct().Should().HaveCount(2);
    }

    private static TestWebHost StartHost(Action<WebApplication> mapEndpoints)
        => TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<RetryProbe>();
                builder.Services.AddScoped<ScopeProbe>();
                builder.Services.AddConduit(conduit => conduit.AddDispatchRetry<TestRetryPolicy>(options =>
                {
                    options.Delays.Clear();
                    options.Delays.Add(new DispatchRetryDelay(TimeSpan.Zero, TimeSpan.Zero));
                    options.Delays.Add(new DispatchRetryDelay(TimeSpan.Zero, TimeSpan.Zero));
                }));
            },
            mapEndpoints);
}

using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

/// <summary>A hub <c>Dispatch</c> reaches the handler through <c>ConduitDispatch</c>, so a retried command is retried.</summary>
[TestClass]
public class DispatchRetryOverTheHubShould
{
    [TestMethod]
    public async Task Retry_a_command_whose_first_attempt_fails_retryably()
    {
        // Arrange
        await using var host = TestWebHost.Start(
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
                builder.Services.AddConduitSignalR();
            },
            app => app.MapHub<ConduitHub>("/conduit"));
        var probe = host.GetRequiredService<RetryProbe>();
        await using var connection = new HubConnectionBuilder().WithUrl($"{host.Address}/conduit").Build();
        await connection.StartAsync();
        var payload = JsonSerializer.SerializeToElement(new { Failures = 1 });

        // Act
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", typeof(FlakyCommand).FullName, payload, noContext, noHeaders);

        // Assert -- two attempts in two scopes, each an attempt of its own (neither is the hub invocation's scope)
        result.Error.Should().BeNull();
        probe.Sightings.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        probe.Sightings.Select(sighting => sighting.ScopeId).Distinct().Should().HaveCount(2);
        result.Data!.Value.GetGuid().Should().Be(probe.Sightings[1].ScopeId);
    }
}

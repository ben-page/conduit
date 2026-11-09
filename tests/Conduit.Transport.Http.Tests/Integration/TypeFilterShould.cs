using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.Http.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.Http.Tests.Integration;

/// <summary>
/// The type filter gates every inbound message, events included, and the publisher hears the refusal: the
/// <c>/publish</c> endpoint answers it as the <c>/dispatch</c> endpoint answers a refused request.
/// </summary>
[TestClass]
public class TypeFilterShould
{
    [TestMethod]
    public async Task RefuseAnUnlistedEventAndAnswerTheCaller()
    {
        // Arrange -- the filter lists a command, not the event
        var spy = new SpyLoggerProvider();
        await using var host = TestWebHost.Start(
            builder =>
            {
                builder.Logging.AddProvider(spy);
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddConduit(conduit =>
                    conduit.AddTypeFilter(filter => filter.AllowType(typeof(CreateItemCommand).FullName!)));
            },
            app => app.MapConduitEndpoint());
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/conduit/publish")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { Id = Guid.NewGuid(), Name = "Unlisted" }, ConduitRuntime.JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Conduit-Type", typeof(ItemCreatedEvent).FullName);

        // Act
        using var response = await client.SendAsync(request);

        // Assert -- a 403 DispatchResult envelope, logged as a refusal and never as an error
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("Forbidden");
        error.GetProperty("message").GetString().Should().Be(
            $"Type '{typeof(ItemCreatedEvent).FullName}' is not allowed by the type filter.");
        spy.Entries.Should().ContainSingle(entry => entry.EventId.Id == 7 && entry.EventId.Name == "RefusedEvent")
            .Which.Should().Match<SpyLogEntry>(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
        spy.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }
}

using System.Text;
using System.Threading.Channels;
using AwesomeAssertions;
using Conduit.Audit.Configuration;
using Conduit.Audit.Runtime;
using Conduit.Audit.Tests.Support;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Audit.Tests.Integration;

/// <summary>
/// <c>AddAudit</c> configures the payload cap: a dispatched body over it is sealed shortened, a disabled cap keeps it whole.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PayloadCapConfigurationShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    private static ServiceProvider BuildProvider(Action<AuditBuilder>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<MessageIdCapture>();
        services.AddConduit(conduit => conduit.AddAudit(configure));
        return services.BuildServiceProvider();
    }

    // The hosted publisher is not started, so the sealed record waits in the channel.
    private static async Task<DispatchAuditRecord> DispatchAndReadAsync(ServiceProvider provider, string value)
    {
        await provider.GetRequiredService<IMediator>().ExecuteAsync(new AuditTestCommand(value));
        provider.GetRequiredService<ChannelReader<DispatchAuditRecord>>().TryRead(out var record).Should().BeTrue();
        return record!;
    }

    private static int Utf8Length(DispatchAuditRecord record) => Encoding.UTF8.GetByteCount(record.Payload!.Value.GetRawText());

    [TestMethod]
    public async Task ApplyTheCapConfiguredOnAddAudit()
    {
        // Arrange
        await using var provider = BuildProvider(audit => audit.MaxPayloadBytes = 1024);

        // Act
        var record = await DispatchAndReadAsync(provider, "start-" + new string('x', 5000));

        // Assert
        provider.GetRequiredService<IOptions<AuditOptions>>().Value.MaxPayloadBytes.Should().Be(1024);
        Utf8Length(record).Should().BeLessThanOrEqualTo(1024);
        record.Payload!.Value.GetProperty("value").GetString().Should().StartWith("start-x").And.EndWith("…");
        record.Payload!.Value.GetProperty("$truncated").GetProperty("cap").GetInt32().Should().Be(1024);
    }

    [TestMethod]
    public async Task ApplyTheDefaultCapWhenNoneIsConfigured()
    {
        // Arrange
        await using var provider = BuildProvider(configure: null);

        // Act
        var record = await DispatchAndReadAsync(provider, new string('x', 100 * 1024));

        // Assert
        Utf8Length(record).Should().BeLessThanOrEqualTo(64 * 1024);
        record.Payload!.Value.GetProperty("$truncated").GetProperty("cap").GetInt32().Should().Be(64 * 1024);
    }

    [TestMethod]
    public async Task StoreTheFullBodyWhenTheCapIsDisabled()
    {
        // Arrange
        await using var provider = BuildProvider(audit => audit.MaxPayloadBytes = 0);
        var value = new string('x', 100 * 1024);

        // Act
        var record = await DispatchAndReadAsync(provider, value);

        // Assert
        record.Payload!.Value.GetProperty("value").GetString().Should().Be(value);
        record.Payload!.Value.TryGetProperty("$truncated", out _).Should().BeFalse();
    }

    [TestMethod]
    public void RefuseACapTooSmallForTheMarker()
    {
        // Arrange
        var builder = new ConduitBuilder(new ServiceCollection());

        // Act
        var act = () => builder.AddAudit(audit => audit.MaxPayloadBytes = 100);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*MaxPayloadBytes*");
    }
}

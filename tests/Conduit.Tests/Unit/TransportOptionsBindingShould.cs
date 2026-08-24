using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Configuration;

namespace Conduit.Tests.Unit;

[TestClass]
public class TransportOptionsBindingShould
{
    private sealed class TestBrokerOptions : BrokerTransportOptions
    {
        public string ConnectionString { get; set; } = string.Empty;

        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

        public Uri? ServiceUri { get; set; }
    }

    private static IConfigurationSection Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("E");

    [TestMethod]
    public void BindScalarsAndPoolsIncludingMessageTypes()
    {
        // Arrange
        var section = Section(new Dictionary<string, string?>
        {
            ["E:ConnectionString"] = "amqp://guest@alpha:5672/",
            ["E:DefaultConcurrency"] = "20",
            ["E:ResponseTimeout"] = "00:00:30",
            ["E:Pools:0:Name"] = "bulk",
            ["E:Pools:0:Concurrency"] = "50",
            ["E:Pools:0:MessageTypes:0"] = "Orders.Commands.Import",
            ["E:Pools:0:MessageTypes:1"] = "Orders.Commands.Reindex",
            ["E:Pools:1:Name"] = "slow",
        });
        var options = new TestBrokerOptions();

        // Act
        ConduitConfigurationBinder.BindTransportOptions(options, section);

        // Assert
        options.ConnectionString.Should().Be("amqp://guest@alpha:5672/");
        options.DefaultConcurrency.Should().Be(20);
        options.ResponseTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.Pools.Should().HaveCount(2);
        options.Pools[0].Concurrency.Should().Be(50);
        options.Pools[0].MessageTypes.Should().Equal("Orders.Commands.Import", "Orders.Commands.Reindex");
        options.Pools[1].MessageTypes.Should().BeEmpty();
    }

    [TestMethod]
    public void UnionConfiguredAndCodeDeclaredMessageTypes()
    {
        // Arrange
        var section = Section(new Dictionary<string, string?>
        {
            ["E:Pools:0:Name"] = "bulk",
            ["E:Pools:0:Concurrency"] = "50",
            ["E:Pools:0:MessageTypes:0"] = "Orders.Commands.Import",
        });
        var options = new TestBrokerOptions();

        // Act
        ConduitConfigurationBinder.BindTransportOptions(options, section);
        options.AddPool("bulk", pool => pool.Handle("Orders.Commands.Reindex"));

        // Assert
        options.Pools.Should().ContainSingle();
        options.Pools[0].Concurrency.Should().Be(50);
        options.Pools[0].MessageTypes.Should().Equal("Orders.Commands.Import", "Orders.Commands.Reindex");
    }

    [TestMethod]
    public void SkipExcludedKeys()
    {
        // Arrange — the binder throws on abstract types, so transports exclude those keys
        var section = Section(new Dictionary<string, string?>
        {
            ["E:ConnectionString"] = "amqp://guest@alpha:5672/",
            ["E:ServiceUri"] = "http://localhost:4566",
        });
        var options = new TestBrokerOptions();

        // Act
        ConduitConfigurationBinder.BindTransportOptions(options, section, "ServiceUri");

        // Assert
        options.ConnectionString.Should().Be("amqp://guest@alpha:5672/");
        options.ServiceUri.Should().BeNull();
    }

    [TestMethod]
    public void RejectAPoolWithNoName()
    {
        // Arrange — a missing required Name binds as empty string rather than throwing
        var section = Section(new Dictionary<string, string?>
        {
            ["E:Pools:0:Concurrency"] = "5",
        });
        var options = new TestBrokerOptions();

        // Act
        var act = () => ConduitConfigurationBinder.BindTransportOptions(options, section);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*E:Pools:0*");
    }

    [TestMethod]
    public void RejectDuplicatePoolNames()
    {
        // Arrange
        var section = Section(new Dictionary<string, string?>
        {
            ["E:Pools:0:Name"] = "bulk",
            ["E:Pools:1:Name"] = "bulk",
        });
        var options = new TestBrokerOptions();

        // Act
        var act = () => ConduitConfigurationBinder.BindTransportOptions(options, section);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*bulk*");
    }

    [TestMethod]
    public void DoNothingWhenTheSectionIsAbsent()
    {
        // Arrange
        var options = new TestBrokerOptions();

        // Act
        ConduitConfigurationBinder.BindTransportOptions(options, null);
        ConduitConfigurationBinder.BindTransportOptions(options, Section(new Dictionary<string, string?>()));

        // Assert
        options.ConnectionString.Should().BeEmpty();
        options.DefaultConcurrency.Should().Be(10);
        options.Pools.Should().BeEmpty();
    }
}

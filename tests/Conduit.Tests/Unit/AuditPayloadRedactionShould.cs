using System.Text.Json;
using AwesomeAssertions;
using Conduit.Attributes;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class AuditPayloadRedactionShould
{
    private sealed record PositionalRequest(int EmployeeId, [DoNotAudit] decimal? ProposedPay, string Reason);

    private sealed class PropertyRequest
    {
        public int Id { get; init; }

        [DoNotAudit]
        public string? Password { get; init; }
    }

    [TestMethod]
    public void DropAMemberMarkedOnTheRecordParameter()
    {
        var json = JsonSerializer.SerializeToElement(new PositionalRequest(7, 21.50m, "rate"), ConduitRuntime.AuditJsonOptions);

        json.TryGetProperty("proposedPay", out _).Should().BeFalse();
        json.GetProperty("employeeId").GetInt32().Should().Be(7);
        json.GetProperty("reason").GetString().Should().Be("rate");
    }

    [TestMethod]
    public void DropAMemberMarkedOnTheProperty()
    {
        var json = JsonSerializer.SerializeToElement(new PropertyRequest { Id = 3, Password = "hunter2" }, ConduitRuntime.AuditJsonOptions);

        json.TryGetProperty("password", out _).Should().BeFalse();
        json.GetProperty("id").GetInt32().Should().Be(3);
    }

    [TestMethod]
    public void LeaveTheWireOptionsUntouched() =>
        JsonSerializer.SerializeToElement(new PositionalRequest(7, 21.50m, "rate"), ConduitRuntime.JsonOptions)
            .GetProperty("proposedPay").GetDecimal().Should().Be(21.50m);
}

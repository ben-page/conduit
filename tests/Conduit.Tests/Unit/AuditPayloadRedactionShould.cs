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

    private sealed class CtorRequest([DoNotAudit] string password)
    {
        public int Id { get; init; }

        public string Password { get; } = password;
    }

    private record BaseRequest(int Id, [DoNotAudit] decimal Pay);

    private sealed record DerivedRequest(int Id, decimal Pay, string Note) : BaseRequest(Id, Pay);

    [TestMethod]
    public void DropAMemberWhoseConstructorParameterDiffersOnlyInCase()
    {
        var json = JsonSerializer.SerializeToElement(new CtorRequest("hunter2") { Id = 1 }, ConduitRuntime.AuditJsonOptions);

        json.TryGetProperty("password", out _).Should().BeFalse();
        json.GetProperty("id").GetInt32().Should().Be(1);
    }

    [TestMethod]
    public void DropAMemberMarkedOnABaseRecordParameter()
    {
        var json = JsonSerializer.SerializeToElement(new DerivedRequest(2, 30m, "n"), ConduitRuntime.AuditJsonOptions);

        json.TryGetProperty("pay", out _).Should().BeFalse();
        json.GetProperty("note").GetString().Should().Be("n");
    }

    private class VirtualBaseRequest
    {
        public int Id { get; init; }

        [DoNotAudit]
        public virtual string? Secret { get; init; }
    }

    private sealed class OverridingRequest : VirtualBaseRequest
    {
        public override string? Secret { get; init; }
    }

    [TestMethod]
    public void DropAnOverrideOfAPropertyMarkedOnItsBaseDeclaration()
    {
        // MemberInfo.IsDefined ignores `inherit` for a property, so the override alone would carry no mark.
        var json = JsonSerializer.SerializeToElement(new OverridingRequest { Id = 4, Secret = "s" }, ConduitRuntime.AuditJsonOptions);

        json.TryGetProperty("secret", out _).Should().BeFalse();
        json.GetProperty("id").GetInt32().Should().Be(4);
    }

    [TestMethod]
    public void LeaveTheWireOptionsUntouched() =>
        JsonSerializer.SerializeToElement(new PositionalRequest(7, 21.50m, "rate"), ConduitRuntime.JsonOptions)
            .GetProperty("proposedPay").GetDecimal().Should().Be(21.50m);
}

using System.Text.Json;
using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public class DispatchResultShould
{
    [TestMethod]
    public void HaveDataAndNoErrorWhenOk()
    {
        var result = DispatchResult<string>.Ok("hello");
        result.Data.Should().Be("hello");
        result.Error.Should().BeNull();
    }

    [TestMethod]
    public void HaveErrorAndNoDataWhenFailed()
    {
        var result = DispatchResult<string>.Fail("NotFound", "Item not found");
        result.Data.Should().BeNull();
        result.Error!.Code.Should().Be("NotFound");
    }

    [TestMethod]
    public void HaveFieldErrorsWhenFailedWithDetails()
    {
        var result = DispatchResult<string>.Fail("Validation", "Invalid input",
        [
            new DispatchErrorDetail("name", "Required"),
            new DispatchErrorDetail("email", "Invalid format"),
        ]);
        result.Error!.Details.Should().HaveCount(2);
        result.Error.Details![0].Field.Should().Be("name");
    }

    [TestMethod]
    public void RoundTripThroughConduitJsonOptions()
    {
        // Arrange -- the shapes the envelope endpoint, the hub and a broker reply write, and a C# client reads back
        var ok = DispatchResult<Receipt>.Ok(new Receipt("order-7", 3));
        var detail = new DispatchErrorDetail("lines[0].sku", "Blocked.") { Text = new RefusalText("SkuBlocked") };
        var refusal = new BlockedItemsRefusal(2, [detail])
        {
            Reference = "ref-123",
            Text = new RefusalText("ItemsBlocked", new Dictionary<string, object?> { ["total"] = 2 }),
        };
        var failed = DispatchResult<Receipt>.Fail(refusal);

        // Act
        var okBack = JsonSerializer.Deserialize<DispatchResult<Receipt>>(
            JsonSerializer.Serialize(ok, ConduitRuntime.JsonOptions), ConduitRuntime.JsonOptions)!;
        var failedJson = JsonSerializer.Serialize(failed, ConduitRuntime.JsonOptions);
        var failedBack = JsonSerializer.Deserialize<DispatchResult<Receipt>>(failedJson, ConduitRuntime.JsonOptions)!;

        // Assert
        okBack.Data.Should().Be(new Receipt("order-7", 3));
        okBack.Error.Should().BeNull();

        failedBack.Data.Should().BeNull();
        var error = failedBack.Error!;
        error.Code.Should().Be(ErrorCode.Conflict.Value);
        error.Message.Should().Be("2 items are blocked.");
        error.Status.Should().Be(409);
        error.Type.Should().Be(typeof(BlockedItemsRefusal).FullName);
        error.Data!.Value.GetProperty("total").GetInt32().Should().Be(2);
        error.Reference.Should().Be("ref-123");
        error.Details.Should().ContainSingle().Which.Should().Be(new DispatchErrorDetail("lines[0].sku", "Blocked."));

        // Text, on the error and on each detail, is the server's own: [JsonIgnore] keeps it off the wire, so it does
        // not round-trip
        failed.Error!.Text.Should().NotBeNull();
        failedJson.Should().NotContain("ItemsBlocked").And.NotContain("SkuBlocked");
        error.Text.Should().BeNull();
        error.Details![0].Text.Should().BeNull();
    }

    private sealed record Receipt(string OrderId, int Lines);

    private sealed class BlockedItemsRefusal(int total, IReadOnlyList<DispatchErrorDetail> details)
        : ConduitException(ErrorCode.Conflict, $"{total} items are blocked.", details)
    {
        public int Total => total;
    }
}

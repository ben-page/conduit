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

    [TestMethod]
    public void RoundTripAnOkResultUnchanged()
    {
        // Arrange
        var ok = DispatchResult<Receipt>.Ok(new Receipt("order-9", 4));

        // Act
        var back = RoundTrip(ok);

        // Assert -- record equality: Data is a record and Error null on both sides
        back.Should().Be(ok);
    }

    [TestMethod]
    public void RoundTripAFailureWithDetailsAndReferenceUnchanged()
    {
        // Arrange -- every wire property of an error: code, message, details, the code's status, a support reference
        var built = DispatchResult<Receipt>.Fail(
            ErrorCode.PreconditionFailed,
            "The order changed since it was read.",
            [new DispatchErrorDetail("eTag", "Stale."), new DispatchErrorDetail(null, "Reload the order.")]);
        var failed = built with { Error = built.Error! with { Reference = "ref-412" } };

        // Act
        var back = RoundTrip(failed);

        // Assert
        back.Should().BeEquivalentTo(failed);
        back.Data.Should().BeNull();
        var error = back.Error!;
        error.Code.Should().Be(ErrorCode.PreconditionFailed.Value);
        error.Message.Should().Be("The order changed since it was read.");
        error.Status.Should().Be(412);
        error.Reference.Should().Be("ref-412");
        error.Type.Should().BeNull();
        error.Data.Should().BeNull();
    }

    [TestMethod]
    public void KeepEachDetailOfAFailureInOrder()
    {
        // Arrange
        DispatchErrorDetail[] details =
        [
            new("name", "Required."),
            new("lines[2].quantity", "Must be positive."),
            new(null, "The order is closed."),
        ];
        var failed = DispatchResult<Receipt>.Fail(ErrorCode.BadRequest, "The order is invalid.", details);

        // Act
        var back = RoundTrip(failed);

        // Assert -- each detail record equal to the one sent, in the order sent
        back.Error!.Details.Should().Equal(details);
    }

    private static DispatchResult<T> RoundTrip<T>(DispatchResult<T> result) =>
        JsonSerializer.Deserialize<DispatchResult<T>>(
            JsonSerializer.Serialize(result, ConduitRuntime.JsonOptions), ConduitRuntime.JsonOptions)!;

    private sealed record Receipt(string OrderId, int Lines);

    private sealed class BlockedItemsRefusal(int total, IReadOnlyList<DispatchErrorDetail> details)
        : ConduitException(ErrorCode.Conflict, $"{total} items are blocked.", details)
    {
        public int Total => total;
    }
}

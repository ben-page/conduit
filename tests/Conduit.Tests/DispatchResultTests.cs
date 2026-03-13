using AwesomeAssertions;
using Conduit.Dispatch;

namespace Conduit.Tests;

[TestClass]
public class DispatchResultTests
{
    [TestMethod]
    public void Ok_HasDataAndNoError()
    {
        var result = DispatchResult<string>.Ok("hello");
        result.Data.Should().Be("hello");
        result.Error.Should().BeNull();
    }

    [TestMethod]
    public void Fail_HasErrorAndNoData()
    {
        var result = DispatchResult<string>.Fail("NotFound", "Item not found");
        result.Data.Should().BeNull();
        result.Error!.Code.Should().Be("NotFound");
    }

    [TestMethod]
    public void Fail_WithDetails_HasFieldErrors()
    {
        var result = DispatchResult<string>.Fail("Validation", "Invalid input",
        [
            new DispatchErrorDetail("name", "Required"),
            new DispatchErrorDetail("email", "Invalid format"),
        ]);
        result.Error!.Details.Should().HaveCount(2);
        result.Error.Details![0].Field.Should().Be("name");
    }
}

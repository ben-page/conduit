using AwesomeAssertions;
using Conduit.Dispatch;

namespace Conduit.Tests;

[TestClass]
public class DispatchResultTests
{
    [TestMethod]
    public void SuccessResult_HasDataAndNoError()
    {
        var result = new DispatchResult<string> { Data = "hello" };
        result.Data.Should().Be("hello");
        result.Error.Should().BeNull();
    }

    [TestMethod]
    public void ErrorResult_HasErrorAndNoData()
    {
        var result = new DispatchResult<string>
        {
            Error = new DispatchError("NotFound", "Item not found")
        };
        result.Data.Should().BeNull();
        result.Error.Code.Should().Be("NotFound");
    }

    [TestMethod]
    public void ErrorWithDetails_HasFieldErrors()
    {
        var result = new DispatchResult<string>
        {
            Error = new DispatchError("Validation", "Invalid input",
            [
                new DispatchErrorDetail("name", "Required"),
                new DispatchErrorDetail("email", "Invalid format"),
            ])
        };
        result.Error.Details.Should().HaveCount(2);
        result.Error.Details[0].Field.Should().Be("name");
    }
}

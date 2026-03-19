using AwesomeAssertions;
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
}

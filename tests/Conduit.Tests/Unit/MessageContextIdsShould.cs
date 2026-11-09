using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public class MessageContextIdsShould
{
    // A correlation id an edge takes from a caller lands in log lines, audit rows, inbox keys and outbound headers, so
    // it must be a plain token: ASCII letters, digits and '-', 1 to 64 of them. Both Guid formats Conduit mints pass.
    [TestMethod]
    public void AcceptOnlyPlainTokens()
    {
        // Arrange
        string[] plain =
        [
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("D"),
            "m-1",
            "0",
            "ABC-xyz-019",
            new string('a', 64),
        ];
        string?[] notPlain =
        [
            null,
            "",
            "a\r\nX-Injected: 1",
            "abc\n",
            "abc\r",
            "a b",
            new string('a', 65),
            "café",
            "١٢",
            "Ａ",
            "a_b",
            "a.b",
            "a:b",
            "{" + Guid.NewGuid() + "}",
        ];

        // Act
        var plainResults = plain.Select(id => (Id: id, Valid: MessageContextIds.IsValid(id))).ToList();
        var notPlainResults = notPlain.Select(id => (Id: id, Valid: MessageContextIds.IsValid(id))).ToList();

        // Assert
        using (new AssertionScope())
        {
            foreach (var (id, valid) in plainResults)
            {
                valid.Should().BeTrue("'{0}' is a plain token", id);
            }

            foreach (var (id, valid) in notPlainResults)
            {
                valid.Should().BeFalse("'{0}' is not a plain token", id?.ReplaceLineEndings("\\n"));
            }
        }
    }
}

using AwesomeAssertions;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class TransportSourceShould
{
    [TestMethod]
    public void HoistAPackageImportAndDropARelativeOne()
    {
        // Arrange
        const string text = "import { A } from 'pkg';\nimport type { B } from './base';\n\nexport const a: A = 1;\n";

        // Act
        var source = TransportSource.Parse("x.ts", text);

        // Assert
        source.PackageImports.Should().Equal("import { A } from 'pkg';");
        source.Body.Should().Equal("export const a: A = 1;");
    }

    [TestMethod]
    public void DropAParentRelativeAndADoubleQuotedRelativeImport()
    {
        // Arrange
        const string text = "import type { B } from '../shared';\nimport type { C } from \"./base\";\n\nexport const a = 1;\n";

        // Act
        var source = TransportSource.Parse("x.ts", text);

        // Assert
        source.PackageImports.Should().BeEmpty();
        source.Body.Should().Equal("export const a = 1;");
    }

    [TestMethod]
    public void KeepTheBodyVerbatimAndTrimOnlyTheBlankLinesAroundIt()
    {
        // Arrange
        const string text = "\n\nexport class A {\n  x = 1;\n\n  y = 2;\n}\n\nexport const b = 2;\n\n\n";

        // Act
        var source = TransportSource.Parse("x.ts", text);

        // Assert
        source.Body.Should().Equal("export class A {", "  x = 1;", "", "  y = 2;", "}", "", "export const b = 2;");
    }

    // A Windows checkout (text=auto) or an editor's BOM must not reach conduit.ts.
    [TestMethod]
    public void ReadCrlfLineEndingsAndAByteOrderMarkAsIfTheyWereLf()
    {
        // Arrange
        const string lf = "import { A } from 'pkg';\n\nexport const a: A = 1;\n";
        var crlfWithBom = "\uFEFF" + lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        // Act
        var fromLf = TransportSource.Parse("x.ts", lf);
        var fromCrlf = TransportSource.Parse("x.ts", crlfWithBom);

        // Assert
        fromCrlf.PackageImports.Should().Equal(fromLf.PackageImports);
        fromCrlf.Body.Should().Equal(fromLf.Body);
    }

    [TestMethod]
    public void RefuseAMultiLineImport()
    {
        // Arrange
        const string text = "import {\n  A,\n} from 'pkg';\n\nexport const a = 1;\n";

        // Act
        var act = () => TransportSource.Parse("x.ts", text);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*x.ts*line 1*");
    }

    [TestMethod]
    public void RefuseAnImportBelowTheBody()
    {
        // Arrange
        const string text = "export const a = 1;\nimport type { B } from './base';\n";

        // Act
        var act = () => TransportSource.Parse("x.ts", text);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*x.ts*line 2*");
    }

    [TestMethod]
    public void RefuseAFileWithNoBody()
    {
        // Act
        var act = () => TransportSource.Parse("x.ts", "import { A } from 'pkg';\n\n");

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*x.ts*");
    }

    [TestMethod]
    public void ReplaceItsDefaultPathWithTheConfiguredOne()
    {
        // Arrange
        var source = TransportSource.Parse("x.ts", "export class T {\n  constructor(private p: string = '/api/conduit') {}\n}\n");

        // Act
        var replaced = source.WithDefaultPath("/api/conduit", "/svc/api");

        // Assert
        replaced.Body.Should().Equal("export class T {", "  constructor(private p: string = '/svc/api') {}", "}");
    }

    [TestMethod]
    public void RefuseADefaultPathThatIsMissing()
    {
        // Arrange
        var source = TransportSource.Parse("x.ts", "export const a = 1;\n");

        // Act
        var act = () => source.WithDefaultPath("/api/conduit", "/svc");

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*x.ts*'/api/conduit'*");
    }

    [TestMethod]
    public void RefuseADefaultPathThatAppearsTwice()
    {
        // Arrange
        var source = TransportSource.Parse("x.ts", "export const a = '/api/conduit';\nexport const b = '/api/conduit';\n");

        // Act
        var act = () => source.WithDefaultPath("/api/conduit", "/svc");

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*x.ts*'/api/conduit'*");
    }

    // '/api/conduit' ends in /conduit' but is not the literal '/conduit'.
    [TestMethod]
    public void NotMistakeALongerPathThatEndsLikeTheDefaultForIt()
    {
        // Arrange
        var source = TransportSource.Parse("x.ts", "export const a = '/api/conduit';\n");

        // Act
        var act = () => source.WithDefaultPath("/conduit", "/hub");

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void LoadEveryEmbeddedTransportFile()
    {
        // Act
        var baseSource = TransportSource.Load("base.ts");
        var http = TransportSource.Load("http.ts");
        var signalR = TransportSource.Load("signalr.ts");

        // Assert
        baseSource.PackageImports.Should().BeEmpty();
        http.PackageImports.Should().BeEmpty();
        signalR.PackageImports.Should().Equal(
            "import { HubConnectionBuilder, HubConnection, HubConnectionState } from '@microsoft/signalr';",
            "import type { IRetryPolicy, RetryContext } from '@microsoft/signalr';");
        // Each transport carries its own path anchor exactly once (WithDefaultPath throws otherwise).
        http.Invoking(source => source.WithDefaultPath("/api/conduit", "/api/conduit")).Should().NotThrow();
        signalR.Invoking(source => source.WithDefaultPath("/conduit", "/conduit")).Should().NotThrow();
    }

    [TestMethod]
    public void RefuseATransportFileThatIsNotEmbedded()
    {
        // Act
        var act = () => TransportSource.Load("missing.ts");

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Conduit.TypeGen.Transport.missing.ts*");
    }
}

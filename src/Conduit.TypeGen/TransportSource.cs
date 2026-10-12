using System.Text;

namespace Conduit.TypeGen;

/// <summary>
/// One of the TypeScript files the transport is written in (<c>Transport/*.ts</c>, embedded in this assembly as
/// <c>Conduit.TypeGen.Transport.&lt;file&gt;</c>), split into what <c>conduit.ts</c> takes from it.
/// </summary>
/// <remarks>
/// A transport file is single-line imports at the top (blank lines between them are fine), then its body. <c>conduit.ts</c> is ONE module, so an import of
/// another transport file (a relative specifier) is dropped, and an import from a package is hoisted to the top of
/// <c>conduit.ts</c>. The body is emitted verbatim. The relative imports exist so each file type-checks on its own
/// (tests/transport-ts).
/// </remarks>
internal sealed class TransportSource
{
    internal const string ResourcePrefix = "Conduit.TypeGen.Transport.";

    private TransportSource(string fileName, IReadOnlyList<string> packageImports, IReadOnlyList<string> body)
    {
        this.FileName = fileName;
        this.PackageImports = packageImports;
        this.Body = body;
    }

    /// <summary>The file's name, e.g. <c>signalr.ts</c>.</summary>
    internal string FileName { get; }

    /// <summary>The file's imports from packages, in file order.</summary>
    internal IReadOnlyList<string> PackageImports { get; }

    /// <summary>Everything below the imports, through the last non-empty line, verbatim.</summary>
    internal IReadOnlyList<string> Body { get; }

    /// <summary>Reads an embedded transport file.</summary>
    internal static TransportSource Load(string fileName)
    {
        var resourceName = ResourcePrefix + fileName;
        using var stream = typeof(TransportSource).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Conduit.TypeGen does not embed '{resourceName}' -- every Transport/*.ts file must be an EmbeddedResource.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(fileName, reader.ReadToEnd());
    }

    /// <summary>Splits a transport file's text. Throws when the file is not in the shape TypeGen can assemble.</summary>
    internal static TransportSource Parse(string fileName, string text)
    {
        var lines = text.TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n');
        var packageImports = new List<string>();
        var index = 0;

        // Blank lines between imports are never emitted, so the scan crosses them; a comment or code line ends the block.
        while (index < lines.Length && (lines[index].Length == 0 || IsImport(lines[index])))
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                index++;
                continue;
            }

            if (!line.EndsWith(';'))
            {
                throw Refuse(fileName, index, "an import must be one line ending in ';' -- TypeGen hoists or drops whole lines");
            }

            var fromIndex = line.LastIndexOf(" from ", StringComparison.Ordinal);
            if (fromIndex < 0)
            {
                throw Refuse(fileName, index, "an import must name its module with 'from'");
            }

            var specifier = line[(fromIndex + " from ".Length)..].TrimEnd(';').Trim().Trim('\'', '"');
            var isRelative = specifier.StartsWith("./", StringComparison.Ordinal)
                || specifier.StartsWith("../", StringComparison.Ordinal);
            if (!isRelative)
            {
                packageImports.Add(line);
            }

            index++;
        }

        while (index < lines.Length && lines[index].Length == 0)
        {
            index++;
        }

        var last = lines.Length - 1;
        while (last >= index && lines[last].Length == 0)
        {
            last--;
        }

        if (last < index)
        {
            throw new InvalidOperationException($"Transport file {fileName} has no body below its imports.");
        }

        var body = lines[index..(last + 1)];
        for (var bodyIndex = 0; bodyIndex < body.Length; bodyIndex++)
        {
            if (IsImport(body[bodyIndex]))
            {
                throw Refuse(fileName, index + bodyIndex, "imports go at the top of a transport file, above its body -- a comment or code line above an import ends the import block");
            }
        }

        return new TransportSource(fileName, packageImports, body);
    }

    /// <summary>
    /// Returns this file with its one default-path literal (<c>'defaultPath'</c>) replaced by <paramref name="path"/>.
    /// The literal is the anchor TypeGen substitutes the configured path at, so it must appear exactly once.
    /// </summary>
    internal TransportSource WithDefaultPath(string defaultPath, string path)
    {
        var literal = "'" + defaultPath + "'";
        var count = 0;
        foreach (var line in this.Body)
        {
            for (var at = line.IndexOf(literal, StringComparison.Ordinal);
                 at >= 0;
                 at = line.IndexOf(literal, at + literal.Length, StringComparison.Ordinal))
            {
                count++;
            }
        }

        if (count != 1)
        {
            throw new InvalidOperationException(
                $"Transport file {this.FileName} must contain the default path {literal} exactly once (found {count}) -- TypeGen replaces it with the configured path.");
        }

        var replacement = "'" + path + "'";
        var body = this.Body.Select(line => line.Replace(literal, replacement, StringComparison.Ordinal)).ToList();
        return new TransportSource(this.FileName, this.PackageImports, body);
    }

    private static bool IsImport(string line) => line.StartsWith("import ", StringComparison.Ordinal);

    private static InvalidOperationException Refuse(string fileName, int lineIndex, string reason) =>
        new($"Transport file {fileName}, line {lineIndex + 1}: {reason}.");
}

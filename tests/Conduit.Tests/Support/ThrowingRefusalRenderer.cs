using System.Globalization;
using Conduit.Exceptions;
using Conduit.Runtime;

namespace Conduit.Tests.Support;

/// <summary>A renderer that throws for a text named <see cref="FailingName"/> and renders every other as <c>"es:" + Name</c>.</summary>
public sealed class ThrowingRefusalRenderer : IRefusalTextRenderer
{
    public const string FailingName = "boom";

    public string? Render(RefusalText text, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Name == FailingName
            ? throw new InvalidOperationException("catalog bug")
            : "es:" + text.Name;
    }
}

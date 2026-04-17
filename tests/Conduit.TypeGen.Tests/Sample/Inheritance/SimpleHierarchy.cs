using Conduit.Attributes;

namespace Conduit.TypeGen.Tests.Sample.Inheritance;

[ClientVisible]
public abstract record PidControlBase
{
    public string TagName { get; init; } = string.Empty;
    public string Connection { get; init; } = string.Empty;
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int FontSize { get; init; }
    public string BorderColor { get; init; } = string.Empty;
}

public sealed record PidValueControl : PidControlBase
{
    public string AlarmTag { get; init; } = string.Empty;
    public string AlarmColor { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
}

public sealed record PidBooleanControl : PidControlBase
{
    public string OnColor { get; init; } = string.Empty;
    public string OffColor { get; init; } = string.Empty;
    public string IconStyle { get; init; } = string.Empty;
}

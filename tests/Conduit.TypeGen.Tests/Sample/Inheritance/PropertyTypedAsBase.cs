using Conduit.Attributes;

namespace Conduit.TypeGen.Tests.Sample.Inheritance;

[ClientVisible]
public abstract record WidgetBase
{
    public string Id { get; init; } = string.Empty;
}

public sealed record TextWidget : WidgetBase
{
    public string Text { get; init; } = string.Empty;
}

public sealed record IconWidget : WidgetBase
{
    public string Icon { get; init; } = string.Empty;
}

[ClientVisible]
public sealed record WidgetContainer
{
    public string Name { get; init; } = string.Empty;
    public IList<WidgetBase> Widgets { get; init; } = new List<WidgetBase>();
}

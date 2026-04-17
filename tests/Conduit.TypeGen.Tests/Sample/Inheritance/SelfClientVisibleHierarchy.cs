using Conduit.Attributes;

namespace Conduit.TypeGen.Tests.Sample.Inheritance;

[ClientVisible]
public abstract record NotifyBase
{
    public string MessageId { get; init; } = string.Empty;
}

[ClientVisible]
public sealed record EmailNotify : NotifyBase
{
    public string ToAddress { get; init; } = string.Empty;
}

[ClientVisible]
public sealed record SmsNotify : NotifyBase
{
    public string PhoneNumber { get; init; } = string.Empty;
}

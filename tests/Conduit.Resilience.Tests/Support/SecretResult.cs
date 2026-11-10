namespace Conduit.Resilience.Tests.Support;

/// <summary>A handler result carrying a value that must never reach an audit observation.</summary>
/// <param name="Secret">The value.</param>
public record SecretResult(string Secret);

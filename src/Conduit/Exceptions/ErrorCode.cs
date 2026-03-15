namespace Conduit.Exceptions;

/// <summary>
/// A machine-readable error code paired with an HTTP status code.
/// Equality is based on <see cref="Value"/> only — Value is the wire identity.
/// </summary>
public record ErrorCode
{
    public string Value { get; }
    public int HttpStatusCode { get; }

    public ErrorCode(string value, int httpStatusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        this.Value = value;
        this.HttpStatusCode = httpStatusCode;
    }

    public virtual bool Equals(ErrorCode? other) => other is not null && this.Value == other.Value;

    public override int GetHashCode() => this.Value.GetHashCode(StringComparison.Ordinal);

    public static readonly ErrorCode NotFound = new("NotFound", 404);
    public static readonly ErrorCode Unauthorized = new("Unauthorized", 401);
    public static readonly ErrorCode Conflict = new("Conflict", 409);
    public static readonly ErrorCode InternalError = new("InternalError", 500);
    public static readonly ErrorCode BadRequest = new("BadRequest", 400);

    public override string ToString() => this.Value;
}

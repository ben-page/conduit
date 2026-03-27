namespace Conduit.Exceptions;

/// <summary>
/// A machine-readable error code paired with an HTTP status code.
/// Equality is based on <see cref="Value"/> only — Value is the wire identity.
/// </summary>
public record ErrorCode
{
    /// <summary>The machine-readable code string used on the wire.</summary>
    public string Value { get; }

    /// <summary>The HTTP status code associated with this error code.</summary>
    public int HttpStatusCode { get; }

    /// <summary>
    /// Creates a new error code with the specified wire value and HTTP status code.
    /// </summary>
    /// <param name="value">The machine-readable code string (e.g. "NotFound").</param>
    /// <param name="httpStatusCode">The corresponding HTTP status code (e.g. 404).</param>
    public ErrorCode(string value, int httpStatusCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        this.Value = value;
        this.HttpStatusCode = httpStatusCode;
    }

    /// <inheritdoc/>
    public virtual bool Equals(ErrorCode? other) => other is not null && this.Value == other.Value;

    /// <inheritdoc/>
    public override int GetHashCode() => this.Value.GetHashCode(StringComparison.Ordinal);

    /// <summary>HTTP 404 — resource not found.</summary>
    public static readonly ErrorCode NotFound = new("NotFound", 404);

    /// <summary>HTTP 401 — authentication required or denied.</summary>
    public static readonly ErrorCode Unauthorized = new("Unauthorized", 401);

    /// <summary>HTTP 409 — conflicting state.</summary>
    public static readonly ErrorCode Conflict = new("Conflict", 409);

    /// <summary>HTTP 500 — unhandled server error.</summary>
    public static readonly ErrorCode InternalError = new("InternalError", 500);

    /// <summary>HTTP 400 — invalid request.</summary>
    public static readonly ErrorCode BadRequest = new("BadRequest", 400);

    private static readonly Dictionary<string, int> BuiltInCodes = new(StringComparer.Ordinal)
    {
        [NotFound.Value] = NotFound.HttpStatusCode,
        [Unauthorized.Value] = Unauthorized.HttpStatusCode,
        [Conflict.Value] = Conflict.HttpStatusCode,
        [InternalError.Value] = InternalError.HttpStatusCode,
        [BadRequest.Value] = BadRequest.HttpStatusCode,
    };

    /// <summary>
    /// Maps a wire error code string to its HTTP status code.
    /// Returns 400 for unrecognized codes.
    /// </summary>
    public static int ToHttpStatusCode(string code) =>
        BuiltInCodes.TryGetValue(code, out var statusCode) ? statusCode : 400;

    /// <inheritdoc/>
    public override string ToString() => this.Value;
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conduit.Runtime;

/// <summary>
/// The result of a dispatch operation, containing either the handler result
/// or structured error information.
/// </summary>
/// <typeparam name="T">The type of the successful result.</typeparam>
public sealed record DispatchResult<T>
{
    [JsonConstructor]
    private DispatchResult() { }

    /// <summary>
    /// The handler result when the dispatch succeeds. <see langword="null"/> on failure.
    /// </summary>
    public T? Data { get; init; }

    /// <summary>
    /// Structured error information when the dispatch fails. <see langword="null"/> on success.
    /// </summary>
    public DispatchError? Error { get; init; }

    /// <summary>
    /// Creates a successful result with the given data.
    /// </summary>
#pragma warning disable CA1000 // Factory methods on generic types are the intended API
    public static DispatchResult<T> Ok(T? data) => new() { Data = data };

    /// <summary>
    /// Creates a failed result with the given error.
    /// </summary>
    public static DispatchResult<T> Fail(string code, string message, IReadOnlyList<DispatchErrorDetail>? details = null)
        => new() { Error = new DispatchError(code, message, details) { Status = Exceptions.ErrorCode.ToHttpStatusCode(code) } };

    /// <summary>
    /// Creates a failed result from an <see cref="Exceptions.ErrorCode"/>.
    /// </summary>
    public static DispatchResult<T> Fail(Exceptions.ErrorCode code, string message, IReadOnlyList<DispatchErrorDetail>? details = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new() { Error = new DispatchError(code.Value, message, details) { Status = code.HttpStatusCode } };
    }

    /// <summary>Creates a failed result from a thrown <see cref="Exceptions.ConduitException"/>: code, message,
    /// details, status, and the subclass's type and data.</summary>
    public static DispatchResult<T> Fail(Exceptions.ConduitException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Fail(exception, exception.Message, exception.Details);
    }

    /// <summary>As <see cref="Fail(Exceptions.ConduitException)"/>, but with the given message and details in place of
    /// the exception's own -- what the runtime passes once it has written them in the reader's language. The exception
    /// is not changed.</summary>
    public static DispatchResult<T> Fail(
        Exceptions.ConduitException exception, string message, IReadOnlyList<DispatchErrorDetail>? details)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var type = exception.GetClientType();
        var data = exception.GetClientData();
        return new()
        {
            Error = new DispatchError(exception.Code.Value, message, details)
            {
                Status = exception.Code.HttpStatusCode,
                Type = type,
                Reference = exception.Reference,
                // A typed refusal's data is never null on the wire -- even when a consumer's GetClientData
                // override returns null -- because the client narrows on Type alone and expects an object.
                Data = data is null
                    ? (type is null ? null : EmptyObject())
                    : JsonSerializer.SerializeToElement(data, ConduitRuntime.JsonOptions),
            },
        };
    }

    private static JsonElement EmptyObject() =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), ConduitRuntime.JsonOptions);
#pragma warning restore CA1000
}

/// <summary>
/// Structured error returned from a failed dispatch operation.
/// </summary>
/// <param name="Code">A machine-readable error code (e.g. "VALIDATION", "NOT_FOUND").</param>
/// <param name="Message">A human-readable error message.</param>
/// <param name="Details">Optional field-level error details.</param>
public sealed record DispatchError(
    string Code,
    string Message,
    IReadOnlyList<DispatchErrorDetail>? Details = null)
{
    /// <summary>The HTTP status the error's <c>ErrorCode</c> declares: its CATEGORY. 0 from a server that predates it.</summary>
    public int Status { get; init; }

    /// <summary>The <c>ConduitException</c> subclass's full type name when <c>ConduitException.IsClientTyped</c> is
    /// true for it (a consumer's refusal, or any subclass that declares data); otherwise null.</summary>
    public string? Type { get; init; }

    /// <summary>That subclass's declared data (<c>ConduitException.GetClientData</c>) when <see cref="Type"/> is
    /// set -- an empty object when the refusal declares no data (or its override returns null); otherwise
    /// null.</summary>
    public JsonElement? Data { get; init; }

    /// <summary>The support reference of an UNEXPECTED failure (the <c>InternalError</c> arm): quotable by the user,
    /// greppable in the log line that carries the same value. Null on a refusal and from a server that predates it.
    /// <see cref="Exceptions.ConduitException.Reference"/> forwards onto it.</summary>
    public string? Reference { get; init; }
}

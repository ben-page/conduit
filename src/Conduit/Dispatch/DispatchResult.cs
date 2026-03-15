using System.Text.Json.Serialization;

namespace Conduit.Dispatch;

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
        => new() { Error = new DispatchError(code, message, details) };

    /// <summary>
    /// Creates a failed result from an <see cref="Conduit.Exceptions.ErrorCode"/>.
    /// </summary>
    public static DispatchResult<T> Fail(Exceptions.ErrorCode code, string message, IReadOnlyList<DispatchErrorDetail>? details = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new() { Error = new DispatchError(code.Value, message, details) };
    }
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
    IReadOnlyList<DispatchErrorDetail>? Details = null);

/// <summary>
/// A single field-level error detail within a <see cref="DispatchError"/>.
/// </summary>
/// <param name="Field">The field that caused the error, or <see langword="null"/> for object-level errors.</param>
/// <param name="Message">A human-readable description of the error.</param>
public sealed record DispatchErrorDetail(string? Field, string Message);

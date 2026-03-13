namespace Conduit;

public sealed record DispatchResult<T>
{
    public T? Data { get; init; }
    public DispatchError? Error { get; init; }
}

public sealed record DispatchError(
    string Code,
    string Message,
    DispatchErrorDetail[]? Details = null);

public sealed record DispatchErrorDetail(string? Field, string Message);

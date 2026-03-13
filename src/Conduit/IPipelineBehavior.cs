namespace Conduit;

public interface IPipelineBehavior<TRequest, TResult>
{
    Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken);
}

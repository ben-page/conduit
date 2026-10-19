using Conduit.Runtime;

namespace Conduit.Middleware;

/// <summary>
/// Marks an <see cref="IPipelineBehavior{TRequest, TResult}"/> that only judges whether a request may run: it calls the next
/// step or throws a refusal, and writes nothing the request depends on (an authorization check, not a transaction, an audit,
/// or a validation that shapes the request). <see cref="ConduitRuntime.PreflightWireRequestAsync"/> runs every pipeline
/// behavior that carries it, in the pipeline's order, and no other -- so a check added to the pipeline anywhere counts there
/// too, and a behavior that is not a pure judgement never runs early. Read from the pipeline's types, in code.
/// </summary>
#pragma warning disable CA1040 // Marker interface by design: PreflightWireRequestAsync reads it from the pipeline's types
public interface IInboundCheckBehavior;
#pragma warning restore CA1040

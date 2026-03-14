namespace Conduit.SourceGen;

/// <summary>
/// Centralized string constants for all type/namespace references emitted by the source generator.
/// Changing a namespace or type name in the core library only requires updating this file.
/// </summary>
internal static class EmitConstants
{
    // Namespaces emitted in generated using directives
    internal const string NamespaceGenerated = "Conduit.Generated";
    internal const string NamespaceConfiguration = "Conduit.Configuration";
    internal const string NamespaceDispatch = "Conduit.Dispatch";
    internal const string NamespaceMiddleware = "Conduit.Middleware";
    internal const string NamespaceMessages = "Conduit.Messages";
    internal const string NamespaceHandlers = "Conduit.Handlers";

    // Core types (fully qualified, used in generated code)
    internal const string IMediator = "Conduit.IMediator";
    internal const string IWireMediator = "Conduit.IWireMediator";
    internal const string Unit = "Conduit.Unit";

    // Dispatch types
    internal const string ConduitRuntime = "Conduit.Dispatch.ConduitRuntime";
    internal const string DispatchEnvelope = "Conduit.Dispatch.DispatchEnvelope";

    // Configuration types
    internal const string ConduitOptions = "Conduit.Configuration.ConduitOptions";
    internal const string ConduitRoute = "Conduit.Configuration.ConduitRoute";

    // Middleware types (used in GetServices<T> calls)
    internal const string IPipelineBehavior = "Conduit.Middleware.IPipelineBehavior";
    internal const string IEventPipelineBehavior = "Conduit.Middleware.IEventPipelineBehavior";
    internal const string IWireInterceptor = "Conduit.Middleware.IWireInterceptor";

    // Message interface casts (for Validate() calls)
    internal const string ICommand = "Conduit.Messages.ICommand";
    internal const string ICommandWithResult = "Conduit.Messages.ICommand";  // same base, generic param appended
    internal const string IQuery = "Conduit.Messages.IQuery";

    // Transport types
    internal const string IConduitTransport = "Conduit.Dispatch.IConduitTransport";
}

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
    internal const string NamespaceRuntime = "Conduit.Runtime";
    internal const string NamespaceMiddleware = "Conduit.Middleware";
    internal const string NamespaceMessages = "Conduit.Messages";
    internal const string NamespaceHandlers = "Conduit.Handlers";
    internal const string NamespaceExceptions = "Conduit.Exceptions";

    // Core types (fully qualified, used in generated code)
    internal const string IMediator = "Conduit.IMediator";
    internal const string IWireMediator = "Conduit.IWireMediator";
    internal const string Unit = "Conduit.Messages.Unit";

    // Runtime types
    internal const string ConduitRuntime = "Conduit.Runtime.ConduitRuntime";
    internal const string DispatchEnvelope = "Conduit.Runtime.DispatchEnvelope";

    // Configuration types
    internal const string ConduitBuilder = "Conduit.Configuration.ConduitBuilder";
    internal const string ConduitOptions = "Conduit.Configuration.ConduitOptions";
    internal const string ConduitTypeRegistry = "Conduit.Runtime.ConduitTypeRegistry";

    // Middleware types
    internal const string IPipelineBehavior = "Conduit.Middleware.IPipelineBehavior";
    internal const string IInboundInterceptor = "Conduit.Middleware.IInboundInterceptor";
    internal const string IOutboundInterceptor = "Conduit.Middleware.IOutboundInterceptor";

    // Message interface casts (for Validate() calls)
    internal const string ICommand = "Conduit.Messages.ICommand";
    internal const string ICommandWithResult = "Conduit.Messages.ICommand";  // same base, generic param appended
    internal const string IQuery = "Conduit.Messages.IQuery";

    // Context types
    internal const string MessageContext = "Conduit.Runtime.MessageContext";

    // Telemetry types
    internal const string ConduitTelemetry = "Conduit.Runtime.ConduitTelemetry";

    // Transport types
    internal const string IConduitTransport = "Conduit.Runtime.IConduitTransport";

    // REST endpoint types (from Conduit.Transport.Http — only referenced in generated code)
    internal const string ConduitRestHandlers = "Conduit.Transport.Http.ConduitRestHandlers";
    internal const string ConduitRestHandlersShort = "ConduitRestHandlers";

    // Resilience types (from Conduit.Resilience — only referenced in generated code)
    internal const string ResilienceBehavior = "Conduit.Resilience.ResilienceBehavior";
}

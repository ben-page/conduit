namespace Conduit.Attributes;

/// <summary>
/// Excludes a handler or event listener from source-generator auto-registration.
/// Use for components wired explicitly via a feature's <c>Add*</c> extension
/// (e.g. <c>LocalAuditListener</c> via <c>AddAuditListener</c>, which also registers its dependencies).
/// </summary>
/// <remarks>
/// The generator skips the type entirely: it is neither registered nor counted as a handler. An event
/// listener registered by hand still receives events, because listeners are resolved from DI at
/// publish time (a broker transport subscribes to its event only once the feature calls
/// <c>ConduitTypeRegistry.ActivateEvent</c>, as <c>AddAuditListener</c> does). A command or query handler
/// is different: the generator emits a local dispatch case only for a message whose handler it
/// discovered, so a command or query whose only handler carries this attribute gets CONDUIT001, and
/// registering that handler by hand does not make it dispatchable locally: it dispatches only over a route.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NotAutoRegisteredAttribute : Attribute;

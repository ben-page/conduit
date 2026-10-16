using System;
using System.Collections.Generic;
using System.Linq;
using Conduit.Analysis;
using Microsoft.CodeAnalysis;

namespace Conduit.SourceGen;

/// <summary>
/// Scans a Roslyn <see cref="Compilation"/> for dispatchable types (commands, queries, events)
/// based on namespace declarations and attributes. Used by the source generator at compile time.
/// </summary>
public static class RoslynScanner
{
    private const string CommandInterfaceName = "Conduit.Messages.ICommand";
    private const string CommandWithResultInterfaceName = "Conduit.Messages.ICommand<TResult>";
    private const string QueryInterfaceName = "Conduit.Messages.IQuery<TResult>";
    private const string EventInterfaceName = "Conduit.Messages.IEvent";
    private const string LocalOnlyAttributeName = "Conduit.Attributes.LocalOnlyAttribute";
    private const string FireAndForgetAttributeName = "Conduit.Attributes.FireAndForgetAttribute";
    private const string DoNotAuditAttributeName = "Conduit.Attributes.DoNotAuditAttribute";
    private const string AuditRedactedAttributeName = "Conduit.Attributes.AuditRedactedAttribute";
    private const string AuditAsAttributeName = "Conduit.Attributes.AuditAsAttribute";
    private const string JsonElementTypeName = "System.Text.Json.JsonElement";
    private const string ContractsAssemblyName = "Conduit.Contracts";
    private const string NotAutoRegisteredAttributeName = "Conduit.Attributes.NotAutoRegisteredAttribute";

    /// <summary>
    /// Scans the compilation for all dispatchable types implementing
    /// <c>ICommand</c>, <c>IQuery</c>, or <c>IEvent</c>.
    /// Types inaccessible to the generated code (private/protected, or nested in such) are excluded.
    /// </summary>
    /// <param name="compilation">The Roslyn compilation to scan.</param>
    /// <returns>The discovered dispatchable types.</returns>
    public static IReadOnlyList<DispatchableTypeInfo> Scan(Compilation compilation) => Scan(compilation, messageSymbols: null);

    /// <summary>
    /// <see cref="Scan(Compilation)"/>, also collecting each discovered message's symbol into
    /// <paramref name="messageSymbols"/> for <see cref="ScanCarriedAuditAsMisuses"/>.
    /// </summary>
    internal static IReadOnlyList<DispatchableTypeInfo> Scan(Compilation compilation, List<INamedTypeSymbol>? messageSymbols)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        var results = new List<DispatchableTypeInfo>();

        foreach (var typeSymbol in GetAllNamedTypes(compilation))
        {
            if (typeSymbol.IsAbstract || (typeSymbol.TypeKind != TypeKind.Class && typeSymbol.TypeKind != TypeKind.Struct))
            {
                continue;
            }

            var kind = DetermineKind(typeSymbol);
            if (kind == null)
            {
                continue;
            }

            if (!ShouldInclude(typeSymbol))
            {
                continue;
            }

            var resultTypeName = GetResultTypeName(typeSymbol, kind.Value);
            var resultFullTypeName = GetResultFullTypeName(typeSymbol, kind.Value);
            var fireAndForgetAttribute = GetAttribute(typeSymbol, FireAndForgetAttributeName);

            results.Add(new DispatchableTypeInfo
            {
                TypeName = typeSymbol.Name,
                FullTypeName = GetMetadataFullName(typeSymbol),
                Namespace = typeSymbol.ContainingNamespace.ToDisplayString(),
                Kind = kind.Value,
                ResultTypeName = resultTypeName,
                ResultFullTypeName = resultFullTypeName,
                IsLocalOnly = HasAttribute(typeSymbol, LocalOnlyAttributeName),
                IsFireAndForget = fireAndForgetAttribute is not null,
                IsFireAndForgetImmediate = GetNamedBoolArgument(fireAndForgetAttribute, "Immediate"),
                IsDoNotAudit = HasAttribute(typeSymbol, DoNotAuditAttributeName),
                AuditMarkConflicts = AuditMarkConflicts(typeSymbol),
                AuditAsMisuses = AuditAsMisuses(typeSymbol),
            });
            messageSymbols?.Add(typeSymbol);
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Scans the compilation (including referenced assemblies) for command/query handlers and
    /// event listeners, mirroring the interface matches used at dispatch time. Handlers that are
    /// not accessible to the generating assembly are excluded; the message types whose only handler
    /// is inaccessible are reported in <see cref="HandlerScanResult.InaccessibleHandlerMessageTypes"/>.
    /// </summary>
    /// <param name="compilation">The Roslyn compilation to scan.</param>
    /// <returns>The discovered handlers, listener groups, and inaccessible-handler message types.</returns>
    public static HandlerScanResult ScanHandlers(Compilation compilation)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        var handlers = new List<HandlerInfo>();
        var listenerGroups = new Dictionary<string, List<string>>();
        var inaccessible = new List<string>();

        foreach (var typeSymbol in GetAllNamedTypes(compilation))
        {
            if (typeSymbol.IsAbstract || typeSymbol.TypeKind != TypeKind.Class)
            {
                continue;
            }

            // Components wired explicitly via a feature's Add* extension (e.g. LocalAuditListener)
            // opt out of auto-registration. The message itself is unaffected — only this
            // handler/listener's auto DI registration is suppressed.
            if (HasAttribute(typeSymbol, NotAutoRegisteredAttributeName))
            {
                continue;
            }

            foreach (var iface in typeSymbol.AllInterfaces)
            {
                var ifaceOriginal = iface.OriginalDefinition.ToDisplayString();

                // Message-type names are join keys against DispatchableTypeInfo.FullTypeName, so they
                // must use the same metadata ('+') form for nested types.
                var messageType = ifaceOriginal switch
                {
                    "Conduit.Handlers.ICommandHandler<TCommand, TResult>" when iface.TypeArguments.Length == 2 => MetadataNameOf(iface.TypeArguments[0]),
                    "Conduit.Handlers.ICommandHandler<TCommand>" when iface.TypeArguments.Length == 1 => MetadataNameOf(iface.TypeArguments[0]),
                    "Conduit.Handlers.IQueryHandler<TQuery, TResult>" when iface.TypeArguments.Length == 2 => MetadataNameOf(iface.TypeArguments[0]),
                    _ => null,
                };

                if (messageType != null)
                {
                    if (!compilation.IsSymbolAccessibleWithin(typeSymbol, compilation.Assembly))
                    {
                        inaccessible.Add(messageType);
                        continue;
                    }

                    handlers.Add(new HandlerInfo
                    {
                        MessageFullTypeName = messageType,
                        HandlerFullTypeName = typeSymbol.ToDisplayString(),
                    });
                }
                else if (ifaceOriginal == "Conduit.Handlers.IEventListener<TEvent>" && iface.TypeArguments.Length == 1)
                {
                    if (!compilation.IsSymbolAccessibleWithin(typeSymbol, compilation.Assembly))
                    {
                        continue; // listeners are optional; an inaccessible one is simply not wired
                    }

                    var eventFullName = MetadataNameOf(iface.TypeArguments[0]);
                    if (!listenerGroups.TryGetValue(eventFullName, out var listeners))
                    {
                        listeners = new List<string>();
                        listenerGroups[eventFullName] = listeners;
                    }

                    listeners.Add(typeSymbol.ToDisplayString());
                }
            }
        }

        var groups = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var kvp in listenerGroups)
        {
            groups[kvp.Key] = kvp.Value.AsReadOnly();
        }

        return new HandlerScanResult(handlers.AsReadOnly(), groups, inaccessible.AsReadOnly());
    }

    private static DispatchableKind? DetermineKind(INamedTypeSymbol typeSymbol)
    {
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var fullName = iface.OriginalDefinition.ToDisplayString();

            if (fullName == CommandWithResultInterfaceName)
            {
                return DispatchableKind.Command;
            }

            if (fullName == QueryInterfaceName)
            {
                return DispatchableKind.Query;
            }

            if (fullName == EventInterfaceName)
            {
                return DispatchableKind.Event;
            }
        }

        // Check for ICommand (void) last since ICommand<T> extends ICommand
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            if (iface.ToDisplayString() == CommandInterfaceName)
            {
                return DispatchableKind.VoidCommand;
            }
        }

        return null;
    }

    private static bool ShouldInclude(INamedTypeSymbol typeSymbol)
    {
        // Skip types that aren't accessible to generated code (private nested types, etc.)
        if (typeSymbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
        {
            return false;
        }

        // Skip nested types in inaccessible containers
        var container = typeSymbol.ContainingType;
        while (container is not null)
        {
            if (container.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
            {
                return false;
            }

            container = container.ContainingType;
        }

        return true;
    }

    private static string? GetResultTypeName(INamedTypeSymbol typeSymbol, DispatchableKind kind)
    {
        if (kind is DispatchableKind.VoidCommand or DispatchableKind.Event)
        {
            return null;
        }

        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var fullName = iface.OriginalDefinition.ToDisplayString();
            if ((kind == DispatchableKind.Command && fullName == CommandWithResultInterfaceName)
                || (kind == DispatchableKind.Query && fullName == QueryInterfaceName))
            {
                if (iface.TypeArguments.Length > 0)
                {
                    return iface.TypeArguments[0].Name;
                }
            }
        }

        return null;
    }

    private static string? GetResultFullTypeName(INamedTypeSymbol typeSymbol, DispatchableKind kind)
    {
        if (kind is DispatchableKind.VoidCommand or DispatchableKind.Event)
        {
            return null;
        }

        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var fullName = iface.OriginalDefinition.ToDisplayString();
            if ((kind == DispatchableKind.Command && fullName == CommandWithResultInterfaceName)
                || (kind == DispatchableKind.Query && fullName == QueryInterfaceName))
            {
                if (iface.TypeArguments.Length > 0)
                {
                    return iface.TypeArguments[0].ToDisplayString();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the metadata-style full name of a type, matching <see cref="System.Type.FullName"/>:
    /// nested types are joined to their containing type with a '+' (e.g. <c>Ns.Outer+Inner</c>),
    /// not the '.' that <c>ISymbol.ToDisplayString()</c> emits. This is the canonical wire
    /// discriminator and <c>ConduitTypeRegistry</c> key — it must agree with the runtime
    /// <c>Type.FullName</c> and <c>Conduit.TypeGen.ReflectionScanner</c> for the same type.
    /// </summary>
    /// <summary>
    /// Returns the metadata-style full name for any type symbol, delegating to
    /// <see cref="GetMetadataFullName(INamedTypeSymbol)"/> for named types (so nested types use the
    /// '+' separator) and falling back to <c>ISymbol.ToDisplayString()</c> otherwise.
    /// </summary>
    private static string MetadataNameOf(ITypeSymbol typeSymbol)
    {
        return typeSymbol is INamedTypeSymbol named
            ? GetMetadataFullName(named)
            : typeSymbol.ToDisplayString();
    }

    private static string GetMetadataFullName(INamedTypeSymbol typeSymbol)
    {
        // Walk the containing-type chain from the innermost type outward, then reverse so the
        // outermost container leads. Joining with '+' mirrors CLR metadata naming.
        var typeChain = new List<string>();
        var current = typeSymbol;
        while (current is not null)
        {
            typeChain.Add(current.Name);
            current = current.ContainingType;
        }

        typeChain.Reverse();
        var nestedName = string.Join("+", typeChain);

        var ns = typeSymbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
        {
            return nestedName;
        }

        return ns.ToDisplayString() + "." + nestedName;
    }

    // The same rule ConduitRuntime.ApplyAuditMemberMarks enforces at runtime: walk the base chain, and bind constructor
    // parameters to properties by name case-insensitively.
    private static string? AuditMarkConflicts(INamedTypeSymbol typeSymbol)
    {
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var redacted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var type = typeSymbol; type is not null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
        {
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                Collect(property, property.Name);
            }

            foreach (var parameter in type.InstanceConstructors.SelectMany(c => c.Parameters))
            {
                Collect(parameter, parameter.Name);
            }
        }

        var conflicts = dropped.Where(redacted.Contains).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return conflicts.Count == 0 ? null : string.Join(", ", conflicts);

        void Collect(ISymbol symbol, string name)
        {
            if (HasAttribute(symbol, DoNotAuditAttributeName))
            {
                dropped.Add(name);
            }

            if (HasAttribute(symbol, AuditRedactedAttributeName))
            {
                redacted.Add(name);
            }
        }
    }

    // The rule ConduitRuntime.ApplyAuditMemberMarks needs to type an [AuditAs] member at capture: the member is a
    // JsonElement (or JsonElement?), and its argument names a string property of the type (base chain included). Walks
    // the base chain as AuditMarkConflicts does; a constructor parameter is checked by its own type.
    private static string? AuditAsMisuses(INamedTypeSymbol typeSymbol)
    {
        var misuses = new SortedSet<string>(StringComparer.Ordinal);
        for (var type = typeSymbol; type is not null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
        {
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                Check(property, property.Name, property.Type);
            }

            foreach (var parameter in type.InstanceConstructors.SelectMany(c => c.Parameters))
            {
                Check(parameter, parameter.Name, parameter.Type);
            }
        }

        return misuses.Count == 0 ? null : string.Join("; ", misuses);

        void Check(ISymbol symbol, string name, ITypeSymbol memberType)
        {
            var auditAs = GetAttribute(symbol, AuditAsAttributeName);
            if (auditAs is null)
            {
                return;
            }

            if (!IsJsonElement(memberType))
            {
                misuses.Add($"{name} is not a JsonElement");
            }

            var typeMember = auditAs.ConstructorArguments.Length == 1 ? auditAs.ConstructorArguments[0].Value as string : null;
            var sibling = typeMember is null ? null : FindProperty(typeSymbol, typeMember);
            if (sibling is null)
            {
                misuses.Add($"{name} names '{typeMember}', which is not a property of the type");
            }
            else if (sibling.Type.SpecialType != SpecialType.System_String)
            {
                misuses.Add($"{name} names '{typeMember}', which is not a string");
            }
        }
    }

    /// <summary>
    /// CONDUIT006 beyond the message itself: every type a discovered message carries -- its members' types (properties and
    /// constructor parameters, base chain included), through nullables, arrays and generic type arguments, transitively --
    /// that could carry Conduit's [AuditAs] (<see cref="CanCarryAuditMarks"/>) and is not itself a message (a message is
    /// checked as one). That reaches a referenced contracts library the generator does not run on, as message discovery
    /// does; a type in an assembly that does not reference Conduit.Contracts (the BCL, third-party packages) is not walked.
    /// Constructor parameters count for public constructors other than a record's copy constructor. Cycles are guarded,
    /// and each type is reported once however many messages reach it. Every member is walked, marked or not: a misapplied [AuditAs] is wrong wherever it sits. A member typed
    /// as an interface or a type parameter is not walked.
    /// </summary>
    internal static IReadOnlyList<CarriedAuditAsMisuse> ScanCarriedAuditAsMisuses(
        Compilation compilation, IReadOnlyList<INamedTypeSymbol> messageSymbols)
    {
        var messages = new HashSet<ITypeSymbol>(messageSymbols, SymbolEqualityComparer.Default);
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var assemblies = new Dictionary<IAssemblySymbol, bool>(SymbolEqualityComparer.Default);
        var pending = new Queue<INamedTypeSymbol>();
        foreach (var message in messageSymbols)
        {
            if (CanCarryAuditMarks(message, compilation, assemblies) && seen.Add(message))
            {
                pending.Enqueue(message);
            }
        }

        var misuses = new SortedDictionary<string, string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (!messages.Contains(type) && AuditAsMisuses(type) is { } misuse)
            {
                misuses[type.IsGenericType ? type.ToDisplayString() : GetMetadataFullName(type)] = misuse;
            }

            for (var declaring = type; declaring is not null && declaring.SpecialType != SpecialType.System_Object; declaring = declaring.BaseType)
            {
                var memberTypes = declaring.GetMembers().OfType<IPropertySymbol>()
                    .Where(property => !property.IsStatic && !property.IsIndexer)
                    .Select(property => property.Type)
                    .Concat(declaring.InstanceConstructors.Where(IsBindingConstructor)
                        .SelectMany(constructor => constructor.Parameters).Select(parameter => parameter.Type));
                foreach (var carried in memberTypes.SelectMany(Constituents))
                {
                    if (CanCarryAuditMarks(carried, compilation, assemblies) && seen.Add(carried))
                    {
                        pending.Enqueue(carried);
                    }
                }
            }
        }

        return misuses.Select(entry => new CarriedAuditAsMisuse { FullTypeName = entry.Key, Misuses = entry.Value }).ToList();
    }

    // A public constructor the serializer could bind, not a record's copy constructor (its one parameter is the type
    // itself, which would make a base record "carried" by every derived one).
    private static bool IsBindingConstructor(IMethodSymbol constructor) =>
        constructor.DeclaredAccessibility == Accessibility.Public
        && !(constructor.Parameters.Length == 1
            && SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, constructor.ContainingType));

    // A type the walk takes: declared in this compilation, or in a referenced assembly that is or references
    // Conduit.Contracts (where [AuditAs] lives). Anything else cannot carry the attribute, so it is not walked. The
    // per-assembly answer is cached for the walk.
    private static bool CanCarryAuditMarks(ITypeSymbol type, Compilation compilation, Dictionary<IAssemblySymbol, bool> assemblies)
    {
        var assembly = type.ContainingAssembly;
        if (assembly is null)
        {
            return false;
        }

        if (!assemblies.TryGetValue(assembly, out var canCarry))
        {
            canCarry = SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly)
                || assembly.Identity.Name == ContractsAssemblyName
                || assembly.Modules.Any(module => module.ReferencedAssemblies.Any(reference => reference.Name == ContractsAssemblyName));
            assemblies[assembly] = canCarry;
        }

        return canCarry;
    }

    // The class and struct types a member type is built from: itself, its element type, a nullable's underlying type,
    // and its generic type arguments, recursively. The walk takes each one's members.
    private static IEnumerable<INamedTypeSymbol> Constituents(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            foreach (var element in Constituents(array.ElementType))
            {
                yield return element;
            }

            yield break;
        }

        if (type is not INamedTypeSymbol named)
        {
            yield break;
        }

        if (named.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
            && named.TypeKind is TypeKind.Class or TypeKind.Struct)
        {
            yield return named;
        }

        foreach (var argument in named.TypeArguments)
        {
            foreach (var constituent in Constituents(argument))
            {
                yield return constituent;
            }
        }
    }

    private static bool IsJsonElement(ITypeSymbol type)
    {
        var underlying = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;
        return underlying.ToDisplayString() == JsonElementTypeName;
    }

    // The runtime reads the sibling with Type.GetProperty: an exact-name, instance property of the type or a base.
    private static IPropertySymbol? FindProperty(INamedTypeSymbol typeSymbol, string name)
    {
        for (var type = typeSymbol; type is not null; type = type.BaseType)
        {
            var property = type.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic);
            if (property is not null)
            {
                return property;
            }
        }

        return null;
    }

    private static bool HasAttribute(ISymbol typeSymbol, string attributeFullName)
    {
        foreach (var attribute in typeSymbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == attributeFullName)
            {
                return true;
            }
        }

        return false;
    }

    private static AttributeData? GetAttribute(ISymbol typeSymbol, string attributeFullName)
    {
        foreach (var attribute in typeSymbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == attributeFullName)
            {
                return attribute;
            }
        }

        return null;
    }

    private static bool GetNamedBoolArgument(AttributeData? attribute, string name)
    {
        if (attribute is null)
        {
            return false;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name)
            {
                return argument.Value.Value is true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(Compilation compilation)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(compilation.GlobalNamespace);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            foreach (var member in current.GetMembers())
            {
                if (member is INamedTypeSymbol typeSymbol)
                {
                    yield return typeSymbol;

                    foreach (var nested in GetNestedTypes(typeSymbol))
                    {
                        yield return nested;
                    }
                }
                else if (member is INamespaceSymbol ns)
                {
                    stack.Push(ns);
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol typeSymbol)
    {
        foreach (var member in typeSymbol.GetTypeMembers())
        {
            yield return member;

            foreach (var nested in GetNestedTypes(member))
            {
                yield return nested;
            }
        }
    }
}

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Conduit.TypeGen;

/// <summary>
/// Reads whether a query's or command's result is declared <c>T?</c> — <c>IQuery&lt;Item?&gt;</c> rather than
/// <c>IQuery&lt;Item&gt;</c>. C# erases a reference type's <c>?</c>: both reflect as the same closed interface. The
/// compiler records it in a <c>NullableAttribute</c> on the type's <c>InterfaceImpl</c> metadata row, or leaves it to the
/// type's <c>NullableContextAttribute</c> when the row's flags equal the context. Reflection (and
/// <see cref="System.Reflection.MetadataLoadContext"/>) exposes no custom attributes for an <c>InterfaceImpl</c> row, so
/// this decodes the row from the assembly file with <see cref="System.Reflection.Metadata"/>, following Roslyn's
/// nullable-metadata spec (<c>docs/features/nullable-metadata.md</c>).
/// </summary>
/// <remarks>
/// Top level only: whether the result itself may be null. Element nullability (<c>IQuery&lt;List&lt;T?&gt;&gt;</c>) is
/// out of scope, as it is for properties. A value-type result is always <see langword="false"/> — <c>int?</c> is
/// <c>Nullable&lt;int&gt;</c>, which the type map already renders <c>number | null</c>. An oblivious result (compiled with
/// nullable disabled, flag 0) is <see langword="false"/>. Opens one <see cref="PEReader"/> per assembly path, kept until
/// <see cref="Dispose"/>.
/// </remarks>
internal sealed class ResultNullabilityReader : IDisposable
{
    private const string NullableAttributeFullName = "System.Runtime.CompilerServices.NullableAttribute";
    private const string NullableContextAttributeFullName = "System.Runtime.CompilerServices.NullableContextAttribute";

    /// <summary>The nullable-metadata flag for an annotated (<c>T?</c>) reference type. 1 is not-null, 0 oblivious.</summary>
    private const byte Annotated = 2;

    private readonly Dictionary<string, PEReader> peReaders = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="messageType"/>'s result — the type argument of its
    /// <paramref name="genericInterfaceFullName"/> implementation — is a reference type declared <c>T?</c>.
    /// </summary>
    /// <param name="messageType">The query or command type; a runtime or <see cref="System.Reflection.MetadataLoadContext"/> type.</param>
    /// <param name="genericInterfaceFullName">The generic interface's metadata full name, e.g. <c>Conduit.Messages.IQuery`1</c>.</param>
    /// <returns><see langword="true"/> only when the result's flag is 2 (annotated).</returns>
    public bool IsResultNullable(Type messageType, string genericInterfaceFullName)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentException.ThrowIfNullOrEmpty(genericInterfaceFullName);

        var reflectedInterface = FindInterface(messageType, genericInterfaceFullName);
        if (reflectedInterface is null || reflectedInterface.GetGenericArguments()[0].IsValueType)
        {
            return false;
        }

        // The row sits on whichever type in the chain declares the interface; a derived record inherits it without one.
        for (var declaringType = messageType; declaringType is not null; declaringType = declaringType.BaseType)
        {
            if (declaringType.IsConstructedGenericType)
            {
                // A generic base's row speaks in its own type parameters; resolving them is out of scope.
                return false;
            }

            if (this.TryReadResultFlag(declaringType, genericInterfaceFullName, out var flag))
            {
                return flag == Annotated;
            }
        }

        return false;
    }

    /// <summary>
    /// The <c>NullableContextAttribute</c> flag on <paramref name="type"/> or, failing that, its nearest declaring type —
    /// the flag every reference type in that scope takes when it carries no <c>NullableAttribute</c> of its own.
    /// <see langword="null"/> when no type in the chain has one.
    /// </summary>
    internal static byte? NullableContextOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        for (var declaring = type; declaring is not null; declaring = declaring.DeclaringType)
        {
            foreach (var attributeData in declaring.GetCustomAttributesData())
            {
                if (attributeData.AttributeType.FullName == NullableContextAttributeFullName
                    && attributeData.ConstructorArguments.Count == 1
                    && attributeData.ConstructorArguments[0].Value is byte flag)
                {
                    return flag;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var peReader in this.peReaders.Values)
        {
            peReader.Dispose();
        }

        this.peReaders.Clear();
    }

    private static Type? FindInterface(Type messageType, string genericInterfaceFullName)
    {
        foreach (var candidate in messageType.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition().FullName == genericInterfaceFullName)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds <paramref name="declaringType"/>'s own <c>InterfaceImpl</c> row for the generic interface and reads the
    /// result's flag from it. <see langword="false"/> when the type declares no such row (the caller climbs to its base).
    /// </summary>
    private bool TryReadResultFlag(Type declaringType, string genericInterfaceFullName, out byte? flag)
    {
        flag = null;

        var metadata = this.MetadataFor(declaringType.Assembly.Location);
        if (metadata is null)
        {
            return false;
        }

        var typeDefinition = metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(declaringType.MetadataToken));
        foreach (var implementationHandle in typeDefinition.GetInterfaceImplementations())
        {
            var implementation = metadata.GetInterfaceImplementation(implementationHandle);
            if (implementation.Interface.Kind != HandleKind.TypeSpecification
                || !IsInstanceOf(metadata, (TypeSpecificationHandle)implementation.Interface, genericInterfaceFullName))
            {
                continue;
            }

            flag = FindNullableAttribute(metadata, implementation.GetCustomAttributes(), out var attributeFlag)
                ? attributeFlag
                : NullableContextOf(declaringType);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The metadata of the assembly at <paramref name="assemblyPath"/>, or <see langword="null"/> when it has no file
    /// (an in-memory assembly's empty <c>Location</c>).
    /// </summary>
    private MetadataReader? MetadataFor(string assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath))
        {
            return null;
        }

        if (!this.peReaders.TryGetValue(assemblyPath, out var peReader))
        {
            // The PEReader owns the stream (no LeaveOpen) and closes it on Dispose.
            peReader = new PEReader(File.OpenRead(assemblyPath));
            this.peReaders.Add(assemblyPath, peReader);
        }

        return peReader.GetMetadataReader();
    }

    /// <summary>
    /// Whether the TypeSpec blob is a one-argument instance of <paramref name="genericInterfaceFullName"/>:
    /// <c>GENERICINST (CLASS|VALUETYPE) TypeDefOrRef argCount …</c> (ECMA-335 II.23.2.12).
    /// </summary>
    private static bool IsInstanceOf(MetadataReader metadata, TypeSpecificationHandle handle, string genericInterfaceFullName)
    {
        var blob = metadata.GetBlobReader(metadata.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
        {
            return false;
        }

        // CLASS and VALUETYPE both read as TypeHandle.
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
        {
            return false;
        }

        var genericDefinition = blob.ReadTypeHandle();
        return blob.ReadCompressedInteger() == 1
            && FullNameOf(metadata, genericDefinition) == genericInterfaceFullName;
    }

    /// <summary>
    /// Finds the <c>NullableAttribute</c> among <paramref name="attributes"/> and decodes the result's flag from it:
    /// <c>NullableAttribute(byte)</c> applies its one flag to every position; <c>NullableAttribute(byte[])</c> lists one
    /// flag per reference-type position in pre-order — index 0 is the interface, index 1 the result. The attribute is
    /// referenced from the framework when it is public there (a <c>MemberReference</c> into <c>System.Runtime</c> on
    /// .NET 10) and synthesized into the compiling assembly otherwise (a <c>MethodDefinition</c>, e.g. a netstandard2.0
    /// contracts library); both are matched by the constructor's declaring type's name. The form is
    /// read from the constructor's signature, not guessed from the value blob. <paramref name="flag"/> is
    /// <see langword="null"/> when the attribute is present but cannot be decoded — which reads as not nullable.
    /// </summary>
    private static bool FindNullableAttribute(MetadataReader metadata, CustomAttributeHandleCollection attributes, out byte? flag)
    {
        flag = null;

        foreach (var attributeHandle in attributes)
        {
            var attribute = metadata.GetCustomAttribute(attributeHandle);

            EntityHandle declaringType;
            BlobHandle constructorSignature;
            if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            {
                var method = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                declaringType = method.GetDeclaringType();
                constructorSignature = method.Signature;
            }
            else if (attribute.Constructor.Kind == HandleKind.MemberReference)
            {
                var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                declaringType = member.Parent;
                constructorSignature = member.Signature;
            }
            else
            {
                continue;
            }

            if (FullNameOf(metadata, declaringType) != NullableAttributeFullName)
            {
                continue;
            }

            flag = DecodeResultFlag(metadata, constructorSignature, attribute.Value);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Decodes the result's flag from a <c>NullableAttribute</c>: the constructor's one parameter (ECMA-335 II.23.2.1)
    /// says <c>byte</c> or <c>byte[]</c>; the value blob (II.23.3) is the prolog <c>0x0001</c>, then the byte, or an
    /// <c>int32</c> count and that many bytes.
    /// </summary>
    private static byte? DecodeResultFlag(MetadataReader metadata, BlobHandle constructorSignature, BlobHandle value)
    {
        var signature = metadata.GetBlobReader(constructorSignature);
        if (signature.ReadSignatureHeader().Kind != SignatureKind.Method
            || signature.ReadCompressedInteger() != 1
            || signature.ReadSignatureTypeCode() != SignatureTypeCode.Void)
        {
            return null;
        }

        var parameter = signature.ReadSignatureTypeCode();
        var isArray = parameter == SignatureTypeCode.SZArray;
        if (isArray)
        {
            parameter = signature.ReadSignatureTypeCode();
        }

        if (parameter != SignatureTypeCode.Byte)
        {
            return null;
        }

        var arguments = metadata.GetBlobReader(value);
        if (arguments.Length < 2 || arguments.ReadUInt16() != 1)
        {
            return null;
        }

        if (!isArray)
        {
            return arguments.RemainingBytes >= 1 ? arguments.ReadByte() : null;
        }

        if (arguments.RemainingBytes < 4)
        {
            return null;
        }

        // -1 is a null array; fewer than two flags cannot reach the result.
        var count = arguments.ReadInt32();
        if (count < 2 || arguments.RemainingBytes < count)
        {
            return null;
        }

        arguments.ReadByte();
        return arguments.ReadByte();
    }

    /// <summary>
    /// The metadata full name of a TypeDef or TypeRef (<c>Namespace.Name</c>, nested types joined with <c>+</c>), as
    /// <see cref="Type.FullName"/> spells it; <see langword="null"/> for any other handle.
    /// </summary>
    private static string? FullNameOf(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
            var name = metadata.GetString(definition.Name);
            var declaring = definition.GetDeclaringType();
            return declaring.IsNil
                ? Qualify(metadata.GetString(definition.Namespace), name)
                : $"{FullNameOf(metadata, declaring)}+{name}";
        }

        if (handle.Kind == HandleKind.TypeReference)
        {
            var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
            var name = metadata.GetString(reference.Name);
            return reference.ResolutionScope.Kind == HandleKind.TypeReference
                ? $"{FullNameOf(metadata, (TypeReferenceHandle)reference.ResolutionScope)}+{name}"
                : Qualify(metadata.GetString(reference.Namespace), name);
        }

        return null;
    }

    private static string Qualify(string typeNamespace, string name) =>
        typeNamespace.Length == 0 ? name : $"{typeNamespace}.{name}";
}

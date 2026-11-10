using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Conduit.Exceptions;
using Conduit.Messages;
using Conduit.Runtime;
using Microsoft.AspNetCore.Http;

namespace Conduit.Transport.Http.Internal;

/// <summary>
/// Binds a command instance from an <see cref="HttpRequest"/> with a
/// <c>multipart/form-data</c> body. It binds exactly the members a JSON body could write, by the same names: the
/// command's JSON contract (<see cref="ConduitRuntime.JsonOptions"/>' <see cref="JsonTypeInfo"/>) drives it, so a
/// <c>[JsonIgnore]</c>d member is never bound, a field matches the member's wire name (<c>[JsonPropertyName]</c>, else
/// the camelCase name) in any letter case, and an indexer or a member with no setter is skipped. The constructor
/// System.Text.Json deserializes with binds first (a record's primary constructor); then each writable member no
/// constructor parameter covered (a class command's settable properties). The single file property binds from the
/// file part of its wire name, else the first file.
/// </summary>
/// <remarks>
/// A field that does not convert throws <see cref="FormatException"/>, <see cref="InvalidCastException"/> or
/// <see cref="OverflowException"/>, which the REST handler answers as 400; a missing required file throws
/// <see cref="BadRequestException"/>.
/// </remarks>
internal static class MultipartCommandBinder
{
    public static async Task<TCommand> BindAsync<TCommand>(
        HttpRequest request,
        CancellationToken cancellationToken)
        where TCommand : class
    {
        var form = await request.ReadFormAsync(cancellationToken);

        var commandType = typeof(TCommand);

        // GetTypeInfo needs the options configured, which the serializer does on their first use; the first request to
        // reach this host may be this one. Idempotent, and exactly what reading a JSON body would do to them.
        ConduitRuntime.JsonOptions.MakeReadOnly(populateMissingResolver: true);
        var contract = ConduitRuntime.JsonOptions.GetTypeInfo(commandType);
        var ctor = contract.ConstructorAttributeProvider as ConstructorInfo
            ?? throw new InvalidOperationException(
                $"{commandType.Name} has no constructor System.Text.Json can deserialize it with.");

        // The file member, only when it is on the wire: an ignored file property is no more bindable than any other.
        var fileProp = FilePropertyMetadata.GetFileProperty(commandType);
        var fileMember = fileProp is null
            ? null
            : contract.Properties.FirstOrDefault(member =>
                IsOnTheWire(member)
                && member.AttributeProvider is PropertyInfo property
                && string.Equals(property.Name, fileProp.Name, StringComparison.Ordinal));

        // Read the file once, whether a constructor parameter or a setter takes it.
        object? fileValue = null;
        if (fileMember is not null)
        {
            var file = form.Files.GetFile(fileMember.Name) ?? form.Files.FirstOrDefault();
            if (file is null && FilePropertyMetadata.IsFileRequired(commandType))
            {
                throw new BadRequestException($"The file '{fileMember.Name}' is required.");
            }

            fileValue = await BuildFileValueAsync(file, fileMember.PropertyType, cancellationToken);
        }

        // Each constructor parameter binds from the member System.Text.Json matched it to. A parameter whose member is
        // ignored takes its default, as a JSON body leaves it.
        var parameterMembers = contract.Properties
            .Where(member => member.AssociatedParameter is { IsMemberInitializer: false } && IsOnTheWire(member))
            .ToDictionary(member => member.AssociatedParameter!.Position);

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            if (!parameterMembers.TryGetValue(i, out var member))
            {
                args[i] = DefaultFor(parameter);
            }
            else if (member == fileMember)
            {
                args[i] = fileValue;
            }
            else if (form.TryGetValue(member.Name, out var values))
            {
                args[i] = ConvertScalar(values.ToString(), parameter.ParameterType, member.Name);
            }
            else
            {
                args[i] = DefaultFor(parameter);
            }
        }

        var command = (TCommand)ctor.Invoke(args);

        // Then each writable member no constructor parameter covered. A member the form does not mention keeps its
        // initializer's value.
        foreach (var member in contract.Properties)
        {
            if (member.Set is null || member.AssociatedParameter is { IsMemberInitializer: false })
            {
                continue;
            }

            if (member == fileMember)
            {
                member.Set(command, fileValue);
            }
            else if (form.TryGetValue(member.Name, out var values))
            {
                member.Set(command, ConvertScalar(values.ToString(), member.PropertyType, member.Name));
            }
        }

        return command;
    }

    // A [JsonIgnore]d member stays in the contract's Properties (a constructor parameter still matches it), but with
    // neither accessor: System.Text.Json neither writes nor reads it, nor binds its constructor parameter.
    private static bool IsOnTheWire(JsonPropertyInfo member) =>
        member.Get is not null || member.Set is not null;

    private static object? DefaultFor(ParameterInfo parameter) =>
        parameter.HasDefaultValue ? parameter.DefaultValue : GetDefault(parameter.ParameterType);

    private static async Task<object?> BuildFileValueAsync(IFormFile? file, Type target, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return null;
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var bytes = ms.ToArray();
        var mediaType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType;
        var binary = BinaryData.FromBytes(bytes, mediaType);

        if (target == typeof(BinaryData))
        {
            return binary;
        }

        // ConduitFile
        var fileName = string.IsNullOrWhiteSpace(file.FileName) ? null : file.FileName;
        return new ConduitFile(binary, fileName);
    }

    private static object? ConvertScalar(string value, Type target, string fieldName)
    {
        if (target == typeof(string))
        {
            return value;
        }

        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying is not null)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return ConvertScalar(value, underlying, fieldName);
        }

        if (target == typeof(Guid))
        {
            return Guid.Parse(value);
        }

        if (target == typeof(int))
        {
            return int.Parse(value, CultureInfo.InvariantCulture);
        }

        if (target == typeof(long))
        {
            return long.Parse(value, CultureInfo.InvariantCulture);
        }

        if (target == typeof(bool))
        {
            return bool.Parse(value);
        }

        if (target.IsEnum)
        {
            return ParseEnum(value, target, fieldName);
        }

        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    // By name, in any letter case, as the JSON wire reads an enum by name only: a number is not a member here either.
    private static object ParseEnum(string value, Type target, string fieldName)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0
            && !char.IsAsciiDigit(trimmed[0])
            && trimmed[0] is not ('-' or '+')
            && Enum.TryParse(target, trimmed, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new FormatException($"'{value}' is not a member of {target.Name} (field '{fieldName}').");
    }

    private static object? GetDefault(Type t) =>
        t.IsValueType ? Activator.CreateInstance(t) : null;
}

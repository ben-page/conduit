using System.Globalization;
using System.Reflection;
using Conduit.Exceptions;
using Conduit.Messages;
using Microsoft.AspNetCore.Http;

namespace Conduit.Transport.Http.Internal;

/// <summary>
/// Binds a command instance from an <see cref="HttpRequest"/> with a
/// <c>multipart/form-data</c> body. Scalar properties come from form fields
/// (case-insensitive name match); the single file property binds from the
/// first <see cref="IFormFile"/>. Constructor parameters bind first (a record's
/// primary constructor); then each settable property no parameter covered, so a
/// class command with a parameterless constructor binds too.
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
        var fileProp = FilePropertyMetadata.GetFileProperty(commandType);

        // Read the file once, whether a constructor parameter or a setter takes it.
        object? fileValue = null;
        if (fileProp is not null)
        {
            var file = form.Files.GetFile(fileProp.Name) ?? form.Files.FirstOrDefault();
            if (file is null && FilePropertyMetadata.IsFileRequired(commandType))
            {
                throw new BadRequestException($"The file '{fileProp.Name}' is required.");
            }

            fileValue = await BuildFileValueAsync(file, fileProp.PropertyType, cancellationToken);
        }

        // The widest public constructor: a record's primary constructor, or a class's parameterless one.
        var ctor = commandType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var name = parameter.Name ?? string.Empty;
            boundNames.Add(name);

            if (fileProp is not null && string.Equals(name, fileProp.Name, StringComparison.OrdinalIgnoreCase))
            {
                args[i] = fileValue;
            }
            else if (form.TryGetValue(name, out var values))
            {
                args[i] = ConvertScalar(values.ToString(), parameter.ParameterType, name);
            }
            else
            {
                args[i] = parameter.HasDefaultValue ? parameter.DefaultValue : GetDefault(parameter.ParameterType);
            }
        }

        var command = (TCommand)ctor.Invoke(args);

        // Then the settable properties no constructor parameter covered. A property the form does not mention keeps its
        // initializer's value.
        foreach (var property in commandType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (boundNames.Contains(property.Name) || property.SetMethod is not { IsPublic: true })
            {
                continue;
            }

            if (fileProp is not null && string.Equals(property.Name, fileProp.Name, StringComparison.Ordinal))
            {
                property.SetValue(command, fileValue);
            }
            else if (form.TryGetValue(property.Name, out var values))
            {
                property.SetValue(command, ConvertScalar(values.ToString(), property.PropertyType, property.Name));
            }
        }

        return command;
    }

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

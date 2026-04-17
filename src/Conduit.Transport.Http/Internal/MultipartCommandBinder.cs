using Conduit.Messages;
using Microsoft.AspNetCore.Http;

namespace Conduit.Transport.Http.Internal;

/// <summary>
/// Binds a command instance from an <see cref="HttpRequest"/> with a
/// <c>multipart/form-data</c> body. Scalar properties come from form fields
/// (case-insensitive name match); the single file property binds from the
/// first <see cref="IFormFile"/>.
/// </summary>
internal static class MultipartCommandBinder
{
    public static async Task<TCommand> BindAsync<TCommand>(
        HttpRequest request,
        CancellationToken cancellationToken)
        where TCommand : class
    {
        var form = await request.ReadFormAsync(cancellationToken);

        var fileProp = FilePropertyMetadata.GetFileProperty(typeof(TCommand));

        // Resolve constructor argument values in declaration order (records have a
        // primary constructor whose parameters match property order).
        var ctor = typeof(TCommand).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var args = ctor.GetParameters().Select(p =>
        {
            if (fileProp is not null && string.Equals(p.Name, fileProp.Name, StringComparison.OrdinalIgnoreCase))
            {
                var file = form.Files.GetFile(fileProp.Name) ?? form.Files.FirstOrDefault();
                return BuildFileValue(file, fileProp.PropertyType);
            }

            if (!form.TryGetValue(p.Name ?? string.Empty, out var values))
            {
                return GetDefault(p.ParameterType);
            }

            return ConvertScalar(values.ToString(), p.ParameterType);
        }).ToArray();

        return (TCommand)ctor.Invoke(args);
    }

    private static object? BuildFileValue(IFormFile? file, Type target)
    {
        if (file is null)
        {
            return null;
        }

        using var ms = new MemoryStream();
        file.CopyTo(ms);
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

    private static object? ConvertScalar(string value, Type target)
    {
        if (target == typeof(string))
        {
            return value;
        }

        if (target == typeof(Guid))
        {
            return Guid.Parse(value);
        }

        if (target == typeof(int))
        {
            return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (target == typeof(long))
        {
            return long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (target == typeof(bool))
        {
            return bool.Parse(value);
        }

        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying is not null)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return ConvertScalar(value, underlying);
        }

        return Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object? GetDefault(Type t) =>
        t.IsValueType ? Activator.CreateInstance(t) : null;
}

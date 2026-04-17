using System.Collections.Concurrent;
using System.Reflection;
using Conduit.Messages;

namespace Conduit.Transport.Http.Internal;

/// <summary>
/// Discovers the single <see cref="BinaryData"/> or <see cref="ConduitFile"/>
/// property on a command type. Throws if more than one file property is found —
/// used both at startup-validation time and at dispatch time.
/// </summary>
internal static class FilePropertyMetadata
{
    private sealed record Entry(PropertyInfo? FileProperty, PropertyInfo[] ScalarProperties);

    private static readonly ConcurrentDictionary<Type, Entry> Cache = new();

    public static PropertyInfo? GetFileProperty(Type commandType) =>
        GetEntry(commandType).FileProperty;

    public static PropertyInfo[] GetScalarProperties(Type commandType) =>
        GetEntry(commandType).ScalarProperties;

    public static void Validate(Type commandType) => _ = GetEntry(commandType);

    private static Entry GetEntry(Type commandType) =>
        Cache.GetOrAdd(commandType, BuildEntry);

    private static Entry BuildEntry(Type commandType)
    {
        var all = commandType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var fileProps = all.Where(IsFileProperty).ToArray();

        if (fileProps.Length > 1)
        {
            var names = string.Join(", ", fileProps.Select(p => p.Name));
            throw new InvalidOperationException(
                $"{commandType.Name} declares {fileProps.Length} file properties ({names}); "
                + "only one file property is supported in v1. "
                + "Use IReadOnlyList<ConduitFile> for multi-file uploads once the feature is implemented.");
        }

        var fileProp = fileProps.FirstOrDefault();
        var scalars = all.Where(p => p != fileProp).ToArray();
        return new Entry(fileProp, scalars);
    }

    private static bool IsFileProperty(PropertyInfo p) =>
        p.PropertyType == typeof(BinaryData) || p.PropertyType == typeof(ConduitFile);
}

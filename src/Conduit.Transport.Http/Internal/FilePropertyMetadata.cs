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
    private sealed record Entry(PropertyInfo? FileProperty, bool FileRequired, PropertyInfo[] ScalarProperties);

    private static readonly ConcurrentDictionary<Type, Entry> Cache = new();

    public static PropertyInfo? GetFileProperty(Type commandType) =>
        GetEntry(commandType).FileProperty;

    /// <summary>
    /// True when the file property is declared non-nullable (<c>ConduitFile</c>, not <c>ConduitFile?</c>): a multipart
    /// request without the file is the caller's mistake, not a null for the handler to trip over.
    /// </summary>
    public static bool IsFileRequired(Type commandType) =>
        GetEntry(commandType).FileRequired;

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

        // NullabilityInfoContext is not thread-safe, so each entry gets its own; the entry is cached per type.
        var fileRequired = fileProp is not null
            && new NullabilityInfoContext().Create(fileProp).WriteState == NullabilityState.NotNull;

        var scalars = all.Where(p => p != fileProp).ToArray();
        return new Entry(fileProp, fileRequired, scalars);
    }

    private static bool IsFileProperty(PropertyInfo p) =>
        p.PropertyType == typeof(BinaryData) || p.PropertyType == typeof(ConduitFile);
}

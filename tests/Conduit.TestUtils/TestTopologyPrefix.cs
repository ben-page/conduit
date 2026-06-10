using System.Globalization;
using System.IO.Hashing;
using System.Reflection;
using System.Text;

namespace Conduit.TestUtils;

/// <summary>
/// Builds Test-mode topology prefixes scoped to a test project so the project can clean up exactly
/// the resources it created. The prefix is <c>test-{hash8}-{random}</c> where <c>hash8</c> is a
/// stable hash of the test assembly's full name: every host in a project shares the
/// <c>test-{hash8}-</c> segment, and the project's cleanup pass deletes everything under it.
/// </summary>
public static class TestTopologyPrefix
{
    /// <summary>An 8-character lowercase-hex stable hash of the assembly's full name.</summary>
    public static string ProjectHash(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var fullName = assembly.FullName ?? assembly.GetName().Name ?? "unknown";
        var hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(fullName));
        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>The shared cleanup prefix for all of a project's resources, ending in '-'.</summary>
    public static string ProjectPrefix(Assembly assembly) => $"test-{ProjectHash(assembly)}-";

    /// <summary>A fresh per-test topology prefix under the project prefix (22 chars).</summary>
    public static string NewForTest(Assembly assembly) =>
        $"{ProjectPrefix(assembly)}{Guid.NewGuid():N}"[..22];
}

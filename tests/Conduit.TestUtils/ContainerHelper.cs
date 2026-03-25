using System.Diagnostics;
using System.Globalization;

namespace Conduit.TestUtils;

/// <summary>
/// Static utilities for container runtime detection, reuse, and cleanup.
/// Used by transport test fixtures to manage broker containers during integration tests.
/// </summary>
public static class ContainerHelper
{
    /// <summary>
    /// Whether containers should be kept alive between test runs.
    /// Controlled by the CONDUIT_REUSE_CONTAINERS environment variable.
    /// </summary>
    public static bool ReuseContainers =>
        Environment.GetEnvironmentVariable("CONDUIT_REUSE_CONTAINERS") == "true";

    /// <summary>
    /// Removes orphaned containers from previous test runs, keeping the newest
    /// running container if CONDUIT_REUSE_CONTAINERS is set.
    /// </summary>
    public static async Task CleanupOrphanedContainersAsync(string label)
    {
        try
        {
            var runtimeName = await DetectContainerRuntimeAsync();
            if (runtimeName is null)
            {
                return;
            }

            // List all containers with our label
            var output = await RunContainerCommandAsync(
                runtimeName,
                $"ps -a --filter label={label} --format {{{{.ID}}}} {{{{.State}}}}");

            if (output is null)
            {
                return;
            }

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return;
            }

            // Parse container info: "id state"
            var containers = lines.Select(line =>
            {
                var parts = line.Split(' ', 2);
                return new { Id = parts[0], State = parts.Length > 1 ? parts[1] : "" };
            }).ToList();

            // If reusing, keep the newest running container
            string? keepId = null;
            if (ReuseContainers)
            {
                keepId = containers.FirstOrDefault(c =>
                    c.State.Equals("running", StringComparison.OrdinalIgnoreCase) ||
                    c.State.StartsWith("Up", StringComparison.OrdinalIgnoreCase))?.Id;
            }

            // Remove all others
            foreach (var container in containers.Where(c => c.Id != keepId))
            {
                await RunContainerCommandAsync(runtimeName, $"rm -f {container.Id}");
            }
        }
        catch
        {
            // Best-effort cleanup — don't fail tests if container runtime isn't available
        }
    }

    /// <summary>
    /// Tries to find an existing running container with the given label and extract
    /// the mapped port. Returns null if no reusable container is found.
    /// </summary>
    public static async Task<(string Hostname, int Port)?> TryReuseContainerAsync(
        string label, int containerPort)
    {
        if (!ReuseContainers)
        {
            return null;
        }

        try
        {
            var runtimeName = await DetectContainerRuntimeAsync();
            if (runtimeName is null)
            {
                return null;
            }

            // Find running container with label
            var containerId = await RunContainerCommandAsync(
                runtimeName,
                $"ps --filter label={label} --filter status=running --format {{{{.ID}}}}");

            if (string.IsNullOrWhiteSpace(containerId))
            {
                return null;
            }

            containerId = containerId.Trim().Split('\n')[0];

            // Get mapped port
            var portOutput = await RunContainerCommandAsync(
                runtimeName,
                $"port {containerId} {containerPort}");

            if (string.IsNullOrWhiteSpace(portOutput))
            {
                return null;
            }

            // Parse "0.0.0.0:12345" -> 12345
            var mappedPort = int.Parse(portOutput.Trim().Split(':').Last(), CultureInfo.InvariantCulture);
            return ("localhost", mappedPort);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Tries to find an existing running container with the given label and extract
    /// multiple mapped ports. Returns null if no reusable container is found.
    /// </summary>
    public static async Task<(string Hostname, Dictionary<int, int> Ports)?> TryReuseContainerMultiPortAsync(
        string label, params int[] containerPorts)
    {
        if (!ReuseContainers)
        {
            return null;
        }

        try
        {
            var runtimeName = await DetectContainerRuntimeAsync();
            if (runtimeName is null)
            {
                return null;
            }

            // Find running container with label
            var containerId = await RunContainerCommandAsync(
                runtimeName,
                $"ps --filter label={label} --filter status=running --format {{{{.ID}}}}");

            if (string.IsNullOrWhiteSpace(containerId))
            {
                return null;
            }

            containerId = containerId.Trim().Split('\n')[0];

            var ports = new Dictionary<int, int>();
            foreach (var containerPort in containerPorts)
            {
                var portOutput = await RunContainerCommandAsync(
                    runtimeName,
                    $"port {containerId} {containerPort}");

                if (string.IsNullOrWhiteSpace(portOutput))
                {
                    return null;
                }

                var mappedPort = int.Parse(portOutput.Trim().Split(':').Last(), CultureInfo.InvariantCulture);
                ports[containerPort] = mappedPort;
            }

            return ("localhost", ports);
        }
        catch
        {
            return null;
        }
    }

    private static string? detectedRuntime;
    private static bool runtimeDetected;

    private static async Task<string?> DetectContainerRuntimeAsync()
    {
        if (runtimeDetected)
        {
            return detectedRuntime;
        }

        foreach (var runtime in new[] { "podman", "docker" })
        {
            try
            {
                var result = await RunContainerCommandAsync(runtime, "version");
                if (result is not null)
                {
                    detectedRuntime = runtime;
                    runtimeDetected = true;
                    return runtime;
                }
            }
            catch
            {
                // Try next runtime
            }
        }

        runtimeDetected = true;
        return null;
    }

    private static async Task<string?> RunContainerCommandAsync(string runtime, string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = runtime,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}

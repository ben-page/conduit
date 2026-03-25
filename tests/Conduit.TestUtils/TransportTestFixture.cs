using System.Diagnostics;
using System.Globalization;
using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.TestUtils;

/// <summary>
/// Abstract base class for multi-host broker transport integration tests.
/// Manages TestHost lifecycle and provides factory methods for creating
/// handler hosts and sender hosts with routing.
/// </summary>
public abstract class TransportTestFixture
{
    private readonly List<TestHost> hosts = [];

    /// <summary>
    /// Whether the underlying broker is available.
    /// Tests should check this and call Assert.Inconclusive when false.
    /// </summary>
    public bool IsAvailable { get; protected set; }

    /// <summary>
    /// Whether containers should be kept alive between test runs.
    /// Controlled by the CONDUIT_REUSE_CONTAINERS environment variable.
    /// </summary>
    protected static bool ReuseContainers =>
        Environment.GetEnvironmentVariable("CONDUIT_REUSE_CONTAINERS") == "true";

    /// <summary>
    /// Removes orphaned containers from previous test runs, keeping the newest
    /// running container if CONDUIT_REUSE_CONTAINERS is set.
    /// </summary>
    protected static async Task CleanupOrphanedContainersAsync(string label)
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
    protected static async Task<(string Hostname, int Port)?> TryReuseContainerAsync(
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
    protected static async Task<(string Hostname, Dictionary<int, int> Ports)?> TryReuseContainerMultiPortAsync(
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

    /// <summary>
    /// Creates a handler/listener host connected to the broker.
    /// The host starts all hosted services (including transport receivers) before returning.
    /// </summary>
    /// <param name="applicationName">Unique application name for queue/topic naming.</param>
    /// <param name="configureHandling">Optional callback to filter which types this host handles.
    /// When null, the host handles all auto-discovered types (full handler host).
    /// Use to simulate a separate application that only handles a subset of types.</param>
    /// <returns>A started TestHost with the transport configured.</returns>
    public async Task<TestHost> CreateHostAsync(
        string applicationName,
        Action<ConduitRegistrationOverrides>? configureHandling = null)
    {
        var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            this.ConfigureTransport(builder, applicationName);

            if (configureHandling is not null)
            {
                builder.Services.ConfigureConduitRegistrations(configureHandling);
            }
        });

        this.hosts.Add(host);
        return host;
    }

    /// <summary>
    /// Creates a sender host that routes a namespace to a target endpoint.
    /// The host starts all hosted services before returning.
    /// </summary>
    /// <param name="applicationName">Unique application name for this sender.</param>
    /// <param name="routeNamespace">Namespace pattern to route (e.g. "MyApp.Commands").</param>
    /// <param name="targetEndpointName">Name of the endpoint to route to.</param>
    /// <returns>A started TestHost with transport and routing configured.</returns>
    public async Task<TestHost> CreateSenderHostAsync(
        string applicationName,
        string routeNamespace,
        string targetEndpointName)
    {
        var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            this.ConfigureSenderTransport(builder, applicationName, routeNamespace, targetEndpointName);
        });

        this.hosts.Add(host);
        return host;
    }

    /// <summary>
    /// Configures a handler/listener host with the broker transport.
    /// Implementations should call builder.Services.AddConduit() with the transport
    /// and set conduit.ApplicationName.
    /// </summary>
    protected abstract void ConfigureTransport(
        HostApplicationBuilder builder,
        string applicationName);

    /// <summary>
    /// Configures a sender host with the broker transport and routing.
    /// Implementations should call builder.Services.AddConduit() with the transport,
    /// set conduit.ApplicationName, and add a route from routeNamespace to targetEndpointName.
    /// </summary>
    protected abstract void ConfigureSenderTransport(
        HostApplicationBuilder builder,
        string applicationName,
        string routeNamespace,
        string targetEndpointName);

    /// <summary>
    /// Tracks a manually-created host so it is disposed during <see cref="DisposeHostsAsync"/>.
    /// Use when tests create hosts directly via <see cref="TestHost.StartAsync"/> instead of
    /// through the fixture's factory methods.
    /// </summary>
    public void TrackHost(TestHost host) => this.hosts.Add(host);

    /// <summary>
    /// Disposes all hosts created during the test. Call from [TestCleanup].
    /// </summary>
    public async Task DisposeHostsAsync()
    {
        foreach (var host in this.hosts)
        {
            await host.DisposeAsync();
        }

        this.hosts.Clear();
    }

    /// <summary>
    /// Generates a short unique application name with the given prefix.
    /// Suitable for broker resource naming (kept short to avoid length limits).
    /// </summary>
    public static string UniqueAppName(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..15];
}

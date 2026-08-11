using ReproStudio.Shared;

namespace ReproStudio_Cli;

/// <summary>
/// Entry point. Parses the command line, handles the one-shot commands, and otherwise
/// hands off to <see cref="ReproSession"/>.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!CliOptions.TryParse(args, out CliOptions options, out string? error))
        {
            Log.Error(error!);
            Log.Blank();
            Log.Raw(CliOptions.Usage);
            return 2;
        }

        if (options.Help)
        {
            Log.Raw(CliOptions.Usage);
            return 0;
        }

        Log.Banner("ReproStudio");
        AppLayout layout = AppLayout.Resolve();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Take over Ctrl+C so the runner gets stopped and the package unregistered,
            // instead of the process being torn down with a registration left behind.
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            if (options.Doctor)
            {
                return await Doctor.ReportAsync(layout, cts.Token).ConfigureAwait(false);
            }

            if (options.List)
            {
                return await ListVersionsAsync(layout, options, cts.Token).ConfigureAwait(false);
            }

            using var session = new ReproSession(options, layout);
            return await session.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
#pragma warning disable CA1031 // Top level: turn any escaped exception into a message plus an exit code.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ListVersionsAsync(AppLayout layout, CliOptions options, CancellationToken ct)
    {
        var provisioner = new RunnerProvisioner(layout.CacheRoot);

        IReadOnlyList<string> versions;
        try
        {
            versions = await provisioner.ListWasdkVersionsAsync(options.Prerelease, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error("Could not reach NuGet: " + ex.Message);
            return 1;
        }

        string provisionedRoot = Path.Combine(layout.CacheRoot, "versions");
        HashSet<string> provisioned = Directory.Exists(provisionedRoot)
            ? Directory.GetDirectories(provisionedRoot)
                .Select(d => Path.GetFileName(d).Split("__")[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        Log.Step("windows app sdk" + (options.Prerelease ? " (including prerelease)" : string.Empty));
        foreach (string version in versions)
        {
            Log.Field(string.Empty, version, provisioned.Contains(version) ? "on disk" : null);
        }

        Log.Blank();
        Log.Detail(versions.Count + " versions. A repro can say '// wasdk: 1.6' and get the newest 1.6.");
        return 0;
    }
}

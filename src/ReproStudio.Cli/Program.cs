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
        using var provisioner = new RunnerProvisioner(layout.CacheRoot);

        IReadOnlyList<string> versions;
        try
        {
            versions = await provisioner.ListWasdkVersionsAsync(options.Prerelease, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (WasdkVersionList.IsExpectedFailure(ex))
        {
            Log.Error("Could not reach NuGet: " + ex.Message);
            return 1;
        }

        WasdkVersionList.Print(versions, layout.CacheRoot, options.Prerelease);
        return 0;
    }
}

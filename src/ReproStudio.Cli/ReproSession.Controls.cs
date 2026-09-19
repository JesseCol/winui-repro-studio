using ReproStudio.Shared;

namespace ReproStudio_Cli;

internal sealed partial class ReproSession
{
    private async Task ProcessControlsAsync(CancellationToken ct)
    {
        Guid lastId = Guid.Empty;
        try
        {
            while (true)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                RunnerControlRequest? command = RunnerControlIo.ReadRequest(_host.RequestPath);
                if (command is null || command.Id == Guid.Empty || command.Id == lastId) continue;
                lastId = command.Id;
                var response = new RunnerControlResponse { Id = command.Id, SessionId = _controlHost.SessionId };
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                TimeSpan remaining = command.ExpiresUtc - DateTime.UtcNow;
                operation.CancelAfter(remaining <= TimeSpan.Zero ? TimeSpan.Zero
                    : remaining > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : remaining);
                Task monitor = CancelAbandonedControlAsync(command, operation);
                try
                {
                    RequireCurrentControl(command);
                    if (command.Operation == "list")
                    {
                        // No gate during network lookup: ordinary editor saves remain live.
                        IReadOnlyList<string> versions = command.Kind switch
                        {
                            "wasdk" => await _provisioner.ListWasdkVersionsAsync(command.IncludePrerelease, operation.Token).ConfigureAwait(false),
                            "winui" => await _provisioner.ListWinUiVersionsAsync(command.IncludePrerelease, operation.Token).ConfigureAwait(false),
                            _ => throw new ArgumentException("Choose Windows App SDK or WinUI."),
                        };
                        response.Versions = versions.ToArray();
                    }
                    else if (command.Operation == "apply")
                    {
                        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
                        try
                        {
                            await ApplyRuntimeControlAsync(command, response, operation.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            _gate.Release();
                        }
                    }
                    else throw new ArgumentException("Unknown Runner control operation.");
                }
                catch (OperationCanceledException)
                {
                    response.Error = "Runtime operation cancelled or timed out. Reopen Runtime to retry.";
                }
#pragma warning disable CA1031 // Report a control-boundary failure without killing the watch loop.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    response.Error = ex.Message;
                    Log.Error("Runner control: " + ex.Message);
                }
                finally
                {
                    await operation.CancelAsync().ConfigureAwait(false);
                    await monitor.ConfigureAwait(false);
                }

                try
                {
                    RunnerControlIo.WriteResponse(_host.RequestPath, response);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Error("Could not acknowledge Runner control: " + ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The watch loop drains this task before disposing its provisioner.
        }
    }

    private async Task CancelAbandonedControlAsync(RunnerControlRequest command, CancellationTokenSource operation)
    {
        try
        {
            while (!operation.IsCancellationRequested)
            {
                await Task.Delay(100, operation.Token).ConfigureAwait(false);
                if (RunnerControlIo.ReadRequest(_host.RequestPath)?.Id != command.Id
                    || _host.ProcessId != command.RunnerProcessId)
                {
                    await operation.CancelAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
    }

    private void RequireCurrentControl(RunnerControlRequest command)
    {
        if (command.SessionId != _controlHost.SessionId || command.ExpiresUtc <= DateTime.UtcNow
            || _host.ProcessId != command.RunnerProcessId
            || command.RenderRequestId != _requestId
            || (command.PairKey is not null && command.PairKey != _runningPair?.Key)
            || RunnerControlIo.ReadRequest(_host.RequestPath)?.Id != command.Id)
        {
            throw new InvalidOperationException("The preview or host session changed. Reopen Runtime and retry.");
        }
    }

    private async Task ApplyRuntimeControlAsync(
        RunnerControlRequest command, RunnerControlResponse response, CancellationToken ct)
    {
        RequireCurrentControl(command);
        string value = command.Value ?? string.Empty;
        RuntimeHeaderEdit.ValidateChoice(command.Kind, value);
        RuntimeHeaderEdit edit;
        try { edit = RuntimeHeaderEdit.Read(_filePath, _requestSourceText); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Cannot update runtime headers in " + _filePath
                + ". Check that the file exists, is writable and is not locked, then retry. " + ex.Message, ex);
        }
        ParsedSnippetFile parsed = SnippetFileParser.Parse(edit.Text);
        WinUiOverride? winui = command.Kind != "winui" ? null
            : value.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                ? WinUiOverride.ForLocalPackage(value) : WinUiOverride.ForVersion(value);
        RunnerPayload? payload = ResolvePayload(parsed.PayloadDir);
        var plan = new LaunchPlan(
            command.Kind == "wasdk" ? value : null,
            await ResolveSdkAsync(command.Sdk, ct).ConfigureAwait(false),
            winui?.CacheKey ?? string.Empty,
            payload?.Fingerprint ?? string.Empty,
            parsed.ProcessLaunchKey,
            _options.Packaged ?? parsed.Packaged ?? false,
            parsed.Dpi ?? 100);

        Log.Event("preparing runtime selection: " + plan.Describe());
        // Deliberately provision even for a reselection. Local packages retain the
        // existing content hash/cache behavior; filenames are not freshness evidence.
        string exe = await _provisioner.EnsureRunnerAsync(
            plan.Version, _layout.BaseRunnerDir, winui, payload,
            new Progress<ProvisionProgress>(p => Log.Detail(p.Message)), ct, plan.Sdk).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        RequireCurrentControl(command);
        string updated = edit.Commit(_filePath, command.Kind, value, plan.Sdk);
        _pendingHeaderReloadText = updated != edit.Text ? updated : null;

        // Only a committed UI choice retires all startup runtime/API overrides.
        _useRuntimeHeaders = true;
        ParsedSnippetFile committed = SnippetFileParser.Parse(updated);
        Snippet snippet = BuildSnippet(committed, plan, winui);
        Log.Detail("Runtime/API headers saved to " + _filePath + ". Startup --wasdk/--winui/--sdk overrides retired for this session.");
        // Success is acknowledged after launch. The old client normally exits first;
        // the new Runner gets the actual selection in its initial render request.
        response.IsRestarting = true;
        if (!await LaunchPreparedAsync(plan, exe, snippet, updated, firstRun: false).ConfigureAwait(false))
            throw new InvalidOperationException("Headers were saved, but the Runner could not start. See the host console; save the file to retry.");
    }
}

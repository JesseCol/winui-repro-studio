using ReproStudio.Shared;

namespace ReproStudio_Runner.Services;

internal sealed class RunnerControlClient(string requestPath)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RunnerControlResponse> SendAsync(
        Snippet snippet, string operation, string kind, bool includePrerelease,
        string? value, CancellationToken ct, string? sdk = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // All process/file polling stays off the XAML dispatcher.
            return await Task.Run(async () =>
            {
                RunnerControlHost host = snippet.ControlHost
                    ?? throw new InvalidOperationException("Runtime changes require a watching host. Run the repro again without --no-watch.");
                if (!host.IsAlive())
                    throw new InvalidOperationException("The host has exited. Run the repro again to change its runtime.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                TimeSpan duration = operation == "apply" ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(45);
                timeout.CancelAfter(duration);
                var command = new RunnerControlRequest
                {
                    SessionId = host.SessionId,
                    RenderRequestId = snippet.RequestId,
                    RunnerProcessId = Environment.ProcessId,
                    ExpiresUtc = DateTime.UtcNow + duration,
                    Operation = operation,
                    Kind = kind,
                    IncludePrerelease = includePrerelease,
                    Value = value,
                    Sdk = sdk,
                    PairKey = snippet.Pair?.Key,
                };
                try
                {
                    RunnerControlIo.WriteRequest(requestPath, command);
                    while (true)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        if (!host.IsAlive())
                            throw new InvalidOperationException("The host exited during the runtime operation. Run the repro again.");
                        RunnerControlResponse? response = RunnerControlIo.ReadResponse(requestPath);
                        if (response?.Id == command.Id && response.SessionId == host.SessionId)
                        {
                            if (response.Error is not null) throw new InvalidOperationException(response.Error);
                            return response;
                        }
                        await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("The host did not finish the runtime operation in time. No further change is requested; reopen Runtime to retry.");
                }
                finally
                {
                    try
                    {
                        if (RunnerControlIo.ReadRequest(requestPath)?.Id == command.Id)
                            File.Delete(RunnerControlIo.RequestPath(requestPath));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        CrashLog.Log("Control cancellation cleanup: " + ex.Message);
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}

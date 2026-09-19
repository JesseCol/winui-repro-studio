using System.Diagnostics;

namespace ReproStudio.Shared;

/// <summary>Identity, not just a recyclable PID. Absent for one-shot hosts.</summary>
public sealed class RunnerControlHost
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public int ProcessId { get; set; } = Environment.ProcessId;
    public long StartTimeUtcTicks { get; set; } = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

    public bool IsAlive()
    {
        try
        {
            using Process process = Process.GetProcessById(ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == StartTimeUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>One operation slot alongside request/result, not a general RPC transport.</summary>
public sealed class RunnerControlRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid RenderRequestId { get; set; }
    public int RunnerProcessId { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string Operation { get; set; } = "list";
    public string Kind { get; set; } = "wasdk";
    public bool IncludePrerelease { get; set; }
    public string? Value { get; set; }
    public string? Sdk { get; set; }
    public string? PairKey { get; set; }
}

public sealed class RunnerControlResponse
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string? Error { get; set; }
    public string[] Versions { get; set; } = [];
    public bool IsRestarting { get; set; }
}

public static class RunnerControlIo
{
    public static string RequestPath(string renderPath) => Path.ChangeExtension(renderPath, ".control.json");
    public static string ResponsePath(string renderPath) => Path.ChangeExtension(renderPath, ".control-result.json");
    public static RunnerControlRequest? ReadRequest(string renderPath) =>
        SnippetIo.TryReadCore<RunnerControlRequest>(RequestPath(renderPath));
    public static RunnerControlResponse? ReadResponse(string renderPath) =>
        SnippetIo.TryReadCore<RunnerControlResponse>(ResponsePath(renderPath));
    public static void WriteRequest(string renderPath, RunnerControlRequest request) =>
        SnippetIo.WriteAtomicCore(RequestPath(renderPath), request);
    public static void WriteResponse(string renderPath, RunnerControlResponse response) =>
        SnippetIo.WriteAtomicCore(ResponsePath(renderPath), response);
}

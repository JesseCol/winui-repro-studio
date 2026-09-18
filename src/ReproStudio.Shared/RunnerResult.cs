namespace ReproStudio.Shared;

/// <summary>A completed render and screenshot, correlated with its original request.</summary>
public sealed class RunnerResult
{
    public Guid RequestId { get; set; }

    public bool RenderSucceeded { get; set; }

    public string? RenderError { get; set; }

    public string? ScreenshotPath { get; set; }

    /// <summary>Windows.Graphics.Capture or RenderTargetBitmap; never an implicit fallback.</summary>
    public string? CaptureMethod { get; set; }

    /// <summary>Why Windows.Graphics.Capture was unavailable when the fallback was used.</summary>
    public string? CaptureWarning { get; set; }

    public string? CaptureError { get; set; }
}

/// <summary>The Runner's response lives next to its per-process request file.</summary>
public static class RunnerResultIo
{
    public static string GetPath(string requestPath) => Path.ChangeExtension(requestPath, ".result.json");

    public static void WriteAtomic(string path, RunnerResult result) => SnippetIo.WriteAtomicCore(path, result);

    public static RunnerResult? TryRead(string path) => SnippetIo.TryReadCore<RunnerResult>(path);
}

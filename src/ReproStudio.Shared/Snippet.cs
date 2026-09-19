namespace ReproStudio.Shared;

/// <summary>
/// A single repro: XAML plus optional C#, and how to host it. One object does
/// triple duty - the saved snippet, the shared artifact, and the IPC request the
/// runner reads off disk.
/// </summary>
public sealed class Snippet
{
    /// <summary>Lets the format evolve without breaking older files.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>IPC correlation for this render request; assigned by the host.</summary>
    public Guid RequestId { get; set; }

    /// <summary>The original .cs file, never the generated request JSON.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Host-resolved, version-independent writable preference file.</summary>
    public string? PreferencesPath { get; set; }

    public RunnerControlHost? ControlHost { get; set; }

    /// <summary>Effective launch selection, including a possible advanced CLI dual override.</summary>
    public string? WinUiToken { get; set; }

    public string? Sdk { get; set; }

    public RunnerPairInfo? Pair { get; set; }

    public string? Title { get; set; }

    public string? Notes { get; set; }

    /// <summary>Launch-time: which Windows App SDK version the runner uses.</summary>
    public string? WasdkVersion { get; set; }

    /// <summary>Launch-time: process DPI for the runner.</summary>
    public int Dpi { get; set; } = 100;

    /// <summary>Live field: Default | Light | Dark.</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>Live field: LeftToRight | RightToLeft.</summary>
    public string FlowDirection { get; set; } = "LeftToRight";

    /// <summary>Live field: optional stage background, e.g. "#202020".</summary>
    public string? Background { get; set; }

    /// <summary>Live field: the XAML to render.</summary>
    public string Xaml { get; set; } = string.Empty;

    /// <summary>Live field: optional C# with a static Setup(FrameworkElement root).</summary>
    public string? CSharp { get; set; }
}

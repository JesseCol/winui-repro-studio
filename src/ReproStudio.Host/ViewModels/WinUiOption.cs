using ReproStudio.Shared;

namespace ReproStudio_Host.ViewModels;

/// <summary>
/// An entry in the WinUI package dropdown: either the default (whatever the chosen
/// Windows App SDK version ships), a specific NuGet WinUI version, or a local
/// .nupkg the user browsed to.
/// </summary>
public sealed class WinUiOption
{
    public required string Display { get; init; }

    /// <summary>Null means "use the WinUI that matches the WASDK version".</summary>
    public WinUiOverride? Override { get; init; }

    public override string ToString() => Display;
}

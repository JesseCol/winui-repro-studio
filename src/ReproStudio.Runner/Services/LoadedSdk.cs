using System.Diagnostics;
using System.Runtime.Loader;
using Microsoft.UI.Xaml;
using ReproStudio.Shared;

namespace ReproStudio_Runner.Services;

internal static class LoadedSdk
{
    private static readonly Lazy<string> Hash = new(() => RunnerPairManifest.HashFile(typeof(Window).Assembly.Location));
    private static string Version => FileVersionInfo.GetVersionInfo(typeof(Window).Assembly.Location).FileVersion ?? "unknown";

    public static bool Matches(Snippet snippet) => snippet.Pair is null
        || (snippet.Pair.WinUiSha256 == Hash.Value
            && snippet.Pair.WinUiAssemblyIdentity == typeof(Window).Assembly.FullName
            && AssemblyLoadContext.GetLoadContext(typeof(Window).Assembly) == AssemblyLoadContext.Default);

    public static string Describe(Snippet snippet) => (snippet.Pair is null ? "Legacy/unspecified API source"
        : Matches(snippet) ? snippet.Pair.ApiLabel : "MISMATCH: requested API was not loaded")
        + " (Microsoft.WinUI " + Version + ")";

    public static string Context(Snippet snippet) => "API: " + Describe(snippet)
        + "; native selection: " + (snippet.Pair?.NativeLabel ?? RuntimeDialog.DescribeSelection(snippet));
}

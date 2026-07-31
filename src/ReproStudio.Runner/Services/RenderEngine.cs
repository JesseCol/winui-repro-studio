using System;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using ReproStudio.Shared;

namespace ReproStudio_Runner.Services;

/// <summary>
/// Outcome of a render attempt: either the live root element, or an error tagged
/// by the phase it happened in (xaml, csharp-compile, or runtime).
/// </summary>
public sealed class RenderResult
{
    public bool Success { get; init; }

    public object? Root { get; init; }

    public string Phase { get; init; } = "xaml";

    public string? Error { get; init; }

    public static RenderResult Ok(object root) => new() { Success = true, Root = root };

    public static RenderResult Fail(string phase, string error) =>
        new() { Success = false, Phase = phase, Error = error };
}

/// <summary>
/// Turns a <see cref="Snippet"/> into a live UI tree: parse the XAML, then (if
/// present) compile the C# and call its static Setup(FrameworkElement root) so it
/// can wire up handlers and data on the parsed tree.
/// </summary>
public sealed class RenderEngine
{
    private readonly RoslynCompiler _compiler = new();

    public RenderResult Render(Snippet snippet, Window window)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        ArgumentNullException.ThrowIfNull(window);

        object root;
        try
        {
            root = XamlReader.Load(snippet.Xaml);
        }
#pragma warning disable CA1031 // Arbitrary user XAML: surface any failure as an error instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return RenderResult.Fail("xaml", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(snippet.CSharp))
        {
            return RenderResult.Ok(root);
        }

        CompileResult compiled = _compiler.Compile(snippet.CSharp!);
        if (!compiled.Success)
        {
            return RenderResult.Fail("csharp-compile", compiled.Error!);
        }

        try
        {
            InvokeSetup(compiled.Assembly!, root, window);
        }
#pragma warning disable CA1031 // Arbitrary user code: surface runtime failures as an error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return RenderResult.Fail("runtime", (ex.InnerException ?? ex).Message);
        }

        return RenderResult.Ok(root);
    }

    /// <summary>
    /// Finds the snippet's static Setup method and calls it, filling each
    /// parameter by type: a Window parameter gets the runner window, and a
    /// FrameworkElement (or compatible) parameter gets the parsed root. So all of
    /// Setup(root), Setup(window), and Setup(root, window) work.
    /// </summary>
    private static void InvokeSetup(Assembly assembly, object root, Window window)
    {
        foreach (Type type in assembly.GetTypes())
        {
            MethodInfo? setup = type.GetMethod(
                "Setup",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (setup is null)
            {
                continue;
            }

            ParameterInfo[] parameters = setup.GetParameters();
            var args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type parameterType = parameters[i].ParameterType;
                args[i] = parameterType.IsInstanceOfType(window) ? window
                    : parameterType.IsInstanceOfType(root) ? root
                    : null;
            }

            setup.Invoke(null, args);
            return;
        }
    }
}

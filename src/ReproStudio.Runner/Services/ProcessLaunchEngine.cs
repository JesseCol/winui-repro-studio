using System.Reflection;
using System.Runtime.ExceptionServices;
using ReproStudio.Shared;

namespace ReproStudio_Runner.Services;

/// <summary>
/// Compiles and invokes the CLI-only <c>OnProcessLaunch</c> hook before XAML starts.
/// The hook is compiled separately from the later live <c>Setup</c> invocation, so it
/// should configure process-wide state rather than share managed static state.
/// </summary>
internal static class ProcessLaunchEngine
{
    public static void Run(string requestPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestPath);

        Snippet snippet = ReadInitialRequest(requestPath);

        if (ProcessLaunchMethod.GetFingerprint(snippet.CSharp ?? string.Empty).Length == 0)
        {
            return;
        }

        var compiler = new RoslynCompiler();
        CompileResult compiled = compiler.Compile(snippet.CSharp!);
        if (!compiled.Success)
        {
            throw new InvalidOperationException(
                $"{ProcessLaunchMethod.Name} could not be compiled:{Environment.NewLine}"
                + compiled.Error);
        }

        MethodInfo[] hooks = compiled.Assembly!
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(method => method.Name == ProcessLaunchMethod.Name)
            .ToArray();
        if (hooks.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one static void {ProcessLaunchMethod.Name}() method, "
                + $"but found {hooks.Length}.");
        }

        MethodInfo hook = hooks[0];
        if (hook.ReturnType != typeof(void) || hook.GetParameters().Length != 0)
        {
            throw new InvalidOperationException(
                $"{ProcessLaunchMethod.Name} must be a parameterless static void method.");
        }

        Action<string>? previousLogSink = ReproApi.LogSink;
        ReproApi.LogSink = message => CrashLog.Log($"{ProcessLaunchMethod.Name}: {message}");
        try
        {
            hook.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
        finally
        {
            ReproApi.LogSink = previousLogSink;
        }
    }

    private static Snippet ReadInitialRequest(string requestPath)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            Snippet? snippet = SnippetIo.TryRead(requestPath);
            if (snippet is not null)
            {
                return snippet;
            }

            Thread.Sleep(20 * attempt);
        }

        throw new InvalidOperationException(
            $"Could not read the initial repro request from {requestPath}.");
    }
}

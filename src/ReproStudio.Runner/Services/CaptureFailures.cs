using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ReproStudio_Runner.Services;

internal static class CaptureFailures
{
    // Device loss, unsupported capture APIs, invalid surfaces and bounded waits are
    // recoverable capture failures. Programming faults are not fallback signals.
    internal static bool IsExpected(Exception exception) =>
        exception is COMException or Win32Exception or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException;

    internal static string Describe(Exception exception) =>
        $"{exception.GetType().Name} (0x{exception.HResult:X8}): {exception.Message}";
}

using System.Runtime.InteropServices;

namespace ReproStudio_Runner.Services;

internal static class XamlOptionalChangesInterop
{
    private const string RuntimeClassName = "Microsoft.UI.Xaml.Settings.XamlOptionalChanges";
    private const int EnableChangeVtableIndex = 6;

    private static readonly Guid XamlOptionalChangesStaticsIid =
        new("EDB65323-1884-51C9-8B79-719554DE4DD9");

    private static readonly Lazy<nint> XamlModule = new(
        () => NativeLibrary.Load(
            Path.Combine(AppContext.BaseDirectory, "Microsoft.UI.Xaml.dll")));

    public static void EnableChange(int changeId)
    {
        nint getFactoryAddress = NativeLibrary.GetExport(
            XamlModule.Value,
            "DllGetActivationFactory");
        var getFactory = Marshal.GetDelegateForFunctionPointer<DllGetActivationFactoryDelegate>(
            getFactoryAddress);

        nint className = 0;
        nint factory = 0;
        nint statics = 0;
        try
        {
            Marshal.ThrowExceptionForHR(
                WindowsCreateString(RuntimeClassName, RuntimeClassName.Length, out className));
            Marshal.ThrowExceptionForHR(getFactory(className, out factory));

            Guid iid = XamlOptionalChangesStaticsIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory, in iid, out statics));

            nint vtable = Marshal.ReadIntPtr(statics);
            nint enableChangeAddress = Marshal.ReadIntPtr(
                vtable,
                EnableChangeVtableIndex * IntPtr.Size);
            var enableChange = Marshal.GetDelegateForFunctionPointer<EnableChangeDelegate>(
                enableChangeAddress);

            Marshal.ThrowExceptionForHR(enableChange(statics, changeId, out byte enabled));
            if (enabled == 0)
            {
                throw new InvalidOperationException(
                    $"Could not enable XAML optional change {changeId} before XAML initialization.");
            }
        }
        finally
        {
            if (statics != 0)
            {
                Marshal.Release(statics);
            }

            if (factory != 0)
            {
                Marshal.Release(factory);
            }

            if (className != 0)
            {
                WindowsDeleteString(className);
            }
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetActivationFactoryDelegate(
        nint activatableClassId,
        out nint activationFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnableChangeDelegate(
        nint thisPtr,
        int changeId,
        out byte result);
}

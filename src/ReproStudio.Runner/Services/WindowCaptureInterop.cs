using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace ReproStudio_Runner.Services;

/// <summary>Thin OS interop only: no dependency on a particular native WinUI runtime.</summary>
internal static class WindowCaptureInterop
{
    private const int DwmwaCloak = 13;
    private const int DwmwaCloaked = 14;
    private const int DwmCloakedApp = 1;
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    internal static void ApplyAppCloak(nint hwnd)
    {
        int cloak = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, DwmwaCloak, in cloak, sizeof(int)));
        VerifyAppCloak(hwnd);

        // Also prevent incidental activation during snippet Setup. Do not change this
        // style on ordinary visible runners.
        Marshal.SetLastPInvokeError(0);
        nint style = Environment.Is64BitProcess
            ? GetWindowLongPtrW(hwnd, GwlExStyle)
            : GetWindowLongW(hwnd, GwlExStyle);
        if (style == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the headless Runner's activation style.");
        }

        Marshal.SetLastPInvokeError(0);
        nint previous = Environment.Is64BitProcess
            ? SetWindowLongPtrW(hwnd, GwlExStyle, style | WsExNoActivate)
            : SetWindowLongW(hwnd, GwlExStyle, (int)style | WsExNoActivate);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot prevent activation of the headless Runner.");
        }

        CrashLog.Log($"Headless HWND 0x{hwnd:X}: DWMWA_CLOAK applied; DWM_CLOAKED_APP verified before Setup/show.");
    }

    internal static void VerifyAppCloak(nint hwnd)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)));
        if ((cloaked & DwmCloakedApp) == 0)
        {
            throw new InvalidOperationException("The Runner HWND is not app-cloaked (DWM_CLOAKED_APP is missing).");
        }
    }

    internal static void FlushDwm() => Marshal.ThrowExceptionForHR(DwmFlush());

    [SupportedOSPlatform("windows10.0.18362")]
    internal static GraphicsCaptureItem CreateCaptureItem(nint hwnd)
    {
        nint name = 0;
        nint factory = 0;
        nint item = 0;
        try
        {
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out name));
            Guid interopIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in interopIid, out factory));
            // IGraphicsCaptureItemInterop is IUnknown-based: CreateForWindow is slot 3.
            nint method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 3 * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(method);
            Guid itemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(create(factory, hwnd, in itemIid, out item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (name != 0) WindowsDeleteString(name);
        }
    }

    internal static IDirect3DDevice CreateDirect3DDevice()
    {
        nint nativeDevice = 0;
        nint dxgiDevice = 0;
        nint inspectable = 0;
        try
        {
            // D3D11_CREATE_DEVICE_BGRA_SUPPORT; SDK_VERSION = 7. Request no immediate
            // context, because SoftwareBitmap performs the surface readback for us.
            int hr = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out nativeDevice, out _, 0);
            if (hr < 0)
            {
                if (nativeDevice != 0)
                {
                    Marshal.Release(nativeDevice);
                    nativeDevice = 0;
                }

                // Hardware may be unavailable in a VM/remote session. WARP is an OS
                // D3D11 device, not a different screenshot backend.
                hr = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out nativeDevice, out _, 0);
            }

            Marshal.ThrowExceptionForHR(hr);
            Guid dxgiIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in dxgiIid, out dxgiDevice));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgiDevice != 0) Marshal.Release(dxgiDevice);
            if (nativeDevice != 0) Marshal.Release(nativeDevice);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowDelegate(nint factory, nint hwnd, in Guid iid, out nint item);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, in int value, int size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowLongW(nint hwnd, int index);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int SetWindowLongW(nint hwnd, int index, int value);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint className, in Guid iid, out nint factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter, int driverType, nint software, uint flags, nint featureLevels,
        uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);
}

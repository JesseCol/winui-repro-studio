using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace ReproStudio_Runner.Services;

/// <summary>
/// One capture session for the lifetime of the Runner, active before each scene
/// update. Starting a new session for each screenshot can return cached pixels.
/// All access is serialized by MainWindow's render/capture task chain.
/// </summary>
internal sealed class WgcFrameSource : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private bool _hasDiscardedInitialFrame;

    private WgcFrameSource(
        IDirect3DDevice device, Direct3D11CaptureFramePool pool, GraphicsCaptureSession session, SizeInt32 size)
    {
        _device = device;
        _pool = pool;
        _session = session;
        Size = size;
    }

    internal SizeInt32 Size { get; private set; }

    internal static WgcFrameSource Create(nint hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            throw new NotSupportedException("WGC HWND interop requires Windows 10 1903 (build 18362); this OS is older.");
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("GraphicsCaptureSession.IsSupported returned false.");
        }

        GraphicsCaptureItem item = WindowCaptureInterop.CreateCaptureItem(hwnd);
        if (item.Size.Width <= 0 || item.Size.Height <= 0)
        {
            throw new InvalidOperationException("WGC reported an empty window.");
        }

        IDirect3DDevice device = WindowCaptureInterop.CreateDirect3DDevice();
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        bool hasTransferredOwnership = false;
        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = pool.CreateCaptureSession(item);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                && ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            {
                session.IsCursorCaptureEnabled = false;
            }

            // No picker, consent request, or uncloak/show workaround.
            session.StartCapture();
            var source = new WgcFrameSource(device, pool, session, item.Size);
            hasTransferredOwnership = true;
            return source;
        }
        finally
        {
            if (!hasTransferredOwnership)
            {
                DisposeResources(session, pool, device);
            }
        }
    }

    internal async Task PrepareForRenderAsync(CancellationToken cancellationToken)
    {
        // Explicitly discard the startup snapshot BEFORE changing the scene. Its
        // timestamp can be new while its pixels still represent an earlier scene.
        while (!_hasDiscardedInitialFrame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Direct3D11CaptureFrame? frame = _pool.TryGetNextFrame();
            if (frame is not null)
            {
                _hasDiscardedInitialFrame = true;
                CrashLog.Log("WGC session active before render; initial cached frame discarded.");
            }
            else
            {
                await Task.Delay(30, cancellationToken);
            }
        }

        // Return idle buffers to the pool before the next scene update, so capture
        // cannot be held up by the previous request's unread frames.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Direct3D11CaptureFrame? frame = _pool.TryGetNextFrame();
            if (frame is null) return;
        }
    }

    internal Direct3D11CaptureFrame? TryGetLatestFrame(CancellationToken cancellationToken)
    {
        Direct3D11CaptureFrame? latest = _pool.TryGetNextFrame();
        bool hasTransferredOwnership = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Direct3D11CaptureFrame? next = _pool.TryGetNextFrame();
                if (next is null)
                {
                    hasTransferredOwnership = true;
                    return latest;
                }

                Direct3D11CaptureFrame? previous = latest;
                latest = next;
                previous?.Dispose();
            }
        }
        finally
        {
            if (!hasTransferredOwnership) latest?.Dispose();
        }
    }

    internal void Resize(SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException("WGC reported an empty resized window.");
        }

        _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        Size = size;
        CrashLog.Log($"WGC frame pool resized to {size.Width}x{size.Height}; waiting for a subsequent frame.");
    }

    public void Dispose() => DisposeResources(_session, _pool, _device);

    private static void DisposeResources(
        GraphicsCaptureSession? session, Direct3D11CaptureFramePool? pool, IDirect3DDevice device)
    {
        try
        {
            session?.Dispose();
        }
        finally
        {
            try
            {
                pool?.Dispose();
            }
            finally
            {
                device.Dispose();
            }
        }
    }
}

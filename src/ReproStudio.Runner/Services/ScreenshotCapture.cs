using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ReproStudio_Runner.Services;

internal sealed record Screenshot(byte[] Png, string Method, string? Warning);

internal sealed class ScreenshotCapture
{
    private static readonly TimeSpan BackendTimeout = TimeSpan.FromSeconds(5);
    private const string FallbackLimitations =
        "RenderTargetBitmap fallback: XAML content only; not a compositor/window-frame screenshot. "
        + "Native/secondary windows, disconnected popups and unsupported XAML visuals may be omitted.";
    private readonly nint _hwnd;
    private Task<WgcFrameSource>? _sourceTask;
    private WgcFrameSource? _source;
    private string? _preparationError;

    internal ScreenshotCapture(nint hwnd)
    {
        _hwnd = hwnd;
    }

    internal async Task PrepareForRenderAsync(FrameworkElement? host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BackendTimeout);
        try
        {
            if (_sourceTask is null)
            {
                // CreateForWindow can reject an HWND that has not been shown yet.
                // Let the ordinary host load while cloaked, then start WGC before
                // executing the first snippet. No artificial scene changes are used.
                if (host is null) throw new InvalidOperationException("Runner content is missing before WGC initialization.");
                await WaitForLayoutAsync(host, timeout.Token);
                _sourceTask = Task.Run(() => WgcFrameSource.Create(_hwnd));
            }

            _source = await _sourceTask.WaitAsync(timeout.Token);
            await Task.Run(() => _source.PrepareForRenderAsync(timeout.Token), cancellationToken);
            _preparationError = null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _preparationError = "WGC could not deliver its initial frame before rendering within 5 seconds.";
        }
        catch (Exception ex) when (CaptureFailures.IsExpected(ex))
        {
            _preparationError = CaptureFailures.Describe(ex);
        }
    }

    internal async Task<Screenshot> CaptureAsync(FrameworkElement root, CancellationToken cancellationToken)
    {
        long renderedAt = Stopwatch.GetTimestamp();
        await WaitForLayoutAsync(root, cancellationToken);
        string warning;
        using var wgcTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wgcTimeout.CancelAfter(BackendTimeout);
        try
        {
            if (_preparationError is not null || _source is null)
            {
                throw new NotSupportedException(_preparationError ?? "The WGC session was not initialized before rendering.");
            }

            root.UpdateLayout();
            // Rendering ticks precede presentation. Commit the current visual tree
            // and wait for outstanding DWM updates off the UI thread before asking
            // WGC for a frame. Never insert capture-only visuals into the repro.
            await ElementCompositionPreview.GetElementVisual(root).Compositor.RequestCommitAsync()
                .AsTask(wgcTimeout.Token).WaitAsync(wgcTimeout.Token);
            await Task.Run(WindowCaptureInterop.FlushDwm, wgcTimeout.Token).WaitAsync(wgcTimeout.Token);

            byte[] png = await Task.Run(() => CaptureWindowAsync(_source, renderedAt, wgcTimeout.Token), cancellationToken);
            return new Screenshot(png, "Windows.Graphics.Capture", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            warning = wgcTimeout.IsCancellationRequested
                ? "Windows.Graphics.Capture timed out after 5 seconds without a usable current frame."
                : "Windows.Graphics.Capture was canceled by Windows before a usable frame was available.";
        }
        catch (Exception ex) when (CaptureFailures.IsExpected(ex))
        {
            CrashLog.Log("WGC capture failure details: " + ex);
            warning = "Windows.Graphics.Capture failed: " + CaptureFailures.Describe(ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        warning += " " + FallbackLimitations;
        CrashLog.Log(warning);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BackendTimeout);
            byte[] png = await CaptureXamlAsync(root, timeout.Token);
            return new Screenshot(png, "RenderTargetBitmap", warning);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(warning + " RenderTargetBitmap also timed out after 5 seconds.");
        }
        catch (Exception ex) when (CaptureFailures.IsExpected(ex))
        {
            throw new InvalidOperationException(warning + " RenderTargetBitmap also failed: " + CaptureFailures.Describe(ex), ex);
        }
    }

    internal async Task CloseAsync()
    {
        // Initialization may still be finishing after a canceled first request.
        // Its owner must retire the device/session even if no screenshot used it.
        if (_sourceTask is null) return;
        WgcFrameSource source = await _sourceTask;
        await Task.Run(source.Dispose);
    }

    private static async Task WaitForLayoutAsync(FrameworkElement root, CancellationToken cancellationToken)
    {
        // Yield out of the constructor/render call so Show/Activate and queued Log
        // callbacks run before layout. Loaded and nonzero dimensions are necessary;
        // a delay alone can capture the old or empty tree.
        await Task.Yield();
        var elapsed = Stopwatch.StartNew();
        while (!root.IsLoaded || root.XamlRoot is null || root.ActualWidth <= 0 || root.ActualHeight <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3))
            {
                throw new TimeoutException("The Runner content did not load with nonzero layout dimensions within 3 seconds.");
            }

            await Task.Delay(30, cancellationToken);
        }

        root.UpdateLayout();
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int ticks = 0;
        void OnRendering(object? sender, object args)
        {
            if (++ticks >= 2) presented.TrySetResult();
        }

        CompositionTarget.Rendering += OnRendering;
        try
        {
            // Rendering is not guaranteed to tick while cloaked. The WGC backend
            // still has to deliver its own fresh frame; RTB renders independently.
            await Task.WhenAny(presented.Task, Task.Delay(500, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            root.UpdateLayout();
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private static async Task<byte[]> CaptureWindowAsync(
        WgcFrameSource source, long renderedAt, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SizeInt32? resized = null;
            using (Direct3D11CaptureFrame? frame = source.TryGetLatestFrame(cancellationToken))
            {
                if (frame is not null && frame.SystemRelativeTime.TotalSeconds >= (double)renderedAt / Stopwatch.Frequency)
                {
                    // This is a subsequent frame from a session already running before
                    // Setup/layout, not the first snapshot of a newly started session.
                    // Timing is an ordering filter, not independent proof of pixel content.
                    if (frame.ContentSize.Width != source.Size.Width || frame.ContentSize.Height != source.Size.Height)
                    {
                        resized = frame.ContentSize;
                    }
                    else
                    {
                        using SoftwareBitmap bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                            frame.Surface, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
                        // Do not publish undefined texture padding as a successful image.
                        if (bitmap.PixelWidth != frame.ContentSize.Width || bitmap.PixelHeight != frame.ContentSize.Height)
                        {
                            throw new InvalidOperationException("The WGC surface and content sizes differ.");
                        }

                        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
                        bitmap.CopyToBuffer(pixels.AsBuffer());
                        return await EncodeAsync(pixels, bitmap.PixelWidth, bitmap.PixelHeight, cancellationToken);
                    }
                }
            }

            if (resized is SizeInt32 size) source.Resize(size);
            await Task.Delay(30, cancellationToken);
        }
    }

    private static async Task<byte[]> CaptureXamlAsync(FrameworkElement root, CancellationToken cancellationToken)
    {
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root).AsTask(cancellationToken);
        byte[] pixels = (await bitmap.GetPixelsAsync().AsTask(cancellationToken)).ToArray();
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0
            || pixels.Length != checked(bitmap.PixelWidth * bitmap.PixelHeight * 4))
        {
            throw new InvalidOperationException("RenderTargetBitmap returned an empty or incomplete pixel buffer.");
        }

        return await EncodeAsync(pixels, bitmap.PixelWidth, bitmap.PixelHeight, cancellationToken);
    }

    private static async Task<byte[]> EncodeAsync(byte[] pixels, int width, int height, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask(cancellationToken);
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync(checked((uint)stream.Size)).AsTask(cancellationToken);
        var png = new byte[checked((int)stream.Size)];
        reader.ReadBytes(png);
        return png;
    }
}

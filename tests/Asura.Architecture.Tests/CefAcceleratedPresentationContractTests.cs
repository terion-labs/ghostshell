namespace Asura.Architecture.Tests;

public sealed class CefAcceleratedPresentationContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Mac_display_link_marshals_rate_limited_frames_to_cef_ui_thread()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "vendor",
            "exclr8cef",
            "native",
            "shim",
            "exclr8cef_mac.mm"));
        var callback = Slice(
            source,
            "CVReturn ExternalBeginFrameDisplayLinkCallback(",
            "void StopExternalBeginFrameClock(");
        var task = Slice(
            source,
            "class ExternalBeginFrameTask final",
            "void EnsureAcceleratedCopyDevice()");

        Assert.Contains("CefPostTask(TID_UI, task)", callback);
        Assert.DoesNotContain("GetHost()->SendExternalBeginFrame()", callback);
        Assert.Contains("GetHost()->SendExternalBeginFrame()", task);
        Assert.Contains("std::min(60.0, clock->nominal_hz)", source);
        Assert.Contains("frame_task_pending.compare_exchange_strong", callback);
    }

    [Fact]
    public void Osr_resize_is_coalesced_on_the_cef_ui_thread()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "vendor",
            "exclr8cef",
            "native",
            "shim",
            "exclr8cef_osr.cc"));
        var setSize = Slice(
            source,
            "void Exclr8CefOsrHandler::SetSize(",
            "void Exclr8CefOsrHandler::QueuePendingSizeOnUi(");
        var queue = Slice(
            source,
            "void Exclr8CefOsrHandler::QueuePendingSizeOnUi(",
            "void Exclr8CefOsrHandler::ApplyPendingSizeOnUi(");
        var apply = Slice(
            source,
            "void Exclr8CefOsrHandler::ApplyPendingSizeOnUi(",
            "void Exclr8CefOsrHandler::SetDeviceScaleFactor(");
        var acceleratedPaint = Slice(
            source,
            "void Exclr8CefOsrHandler::OnAcceleratedPaint(",
            "void Exclr8CefOsrHandler::GetTouchHandleSize(");

        Assert.DoesNotContain("WasResized()", setSize);
        Assert.Contains("resize_task_pending_.compare_exchange_strong", queue);
        Assert.Contains("CefPostTask(TID_UI, task)", queue);
        Assert.Contains("GetHost()->WasResized()", apply);
        Assert.Contains("CefPostDelayedTask(", apply);
        Assert.Contains("GetHost()->Invalidate(PET_VIEW)", apply);
        Assert.Contains("QueueSettledSizeOnUi()", acceleratedPaint);
        Assert.Contains("info.extra.coded_size", acceleratedPaint);
    }

    [Fact]
    public void Mac_accelerated_frames_reuse_only_compositor_released_buffers()
    {
        var nativeSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "vendor",
            "exclr8cef",
            "native",
            "shim",
            "exclr8cef_mac.mm"));
        var copy = Slice(
            nativeSource,
            "extern \"C\" int excef_copy_macos_accelerated_frame(",
            "extern \"C\" int excef_macos_accelerated_frame_is_released(");
        Assert.Contains("can_reuse_destination", copy);
        Assert.Contains("out_frame->destination_texture", copy);
        Assert.Contains("out_frame->ready_value + 2", copy);

        var slotSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "vendor",
            "exclr8cef",
            "src",
            "Exclr8Cef.WebView",
            "AcceleratedFrameSlot.cs"));
        Assert.Contains("AwaitingConsumer", slotSource);
        Assert.Contains("_frame.IsReleasedByConsumer", slotSource);
        Assert.Contains("_importedSurfaceHandle == surfaceHandle", slotSource);
        Assert.Contains("_importedEventHandle == eventHandle", slotSource);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Expected source to contain '{start}'.");
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Expected source to contain '{end}'.");
        return source[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the Asura repository root.");
    }
}

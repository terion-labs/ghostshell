namespace Asura.Application;

/// <summary>
/// Binds physical browser input to the authoritative interactive attachment.
/// The synchronous gate must run before an Avalonia input event reaches CEF so
/// a human can preempt an in-flight one-action agent lease.
/// </summary>
public interface IBrowserPhysicalInputBarrier
{
    void BindPhysicalInputGate(
        Func<NativeRendererPhysicalInput, bool>? physicalInputGate);
}

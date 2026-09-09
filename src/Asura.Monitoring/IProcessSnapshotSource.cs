namespace Asura.Monitoring;

internal interface IProcessSnapshotSource
{
    ValueTask<RawProcessCapture> CaptureAsync(CancellationToken cancellationToken);
}

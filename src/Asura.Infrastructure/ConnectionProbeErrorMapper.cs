using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

internal static class ConnectionProbeErrorMapper
{
    public static ConnectionRuntimeError Map(
        ConnectionProbeResult result,
        ConnectionKind kind)
    {
        if (result.Outcome == ConnectionProbeOutcome.Cancelled)
        {
            return Error(ConnectionRuntimeErrorCode.Cancelled);
        }

        if (result.Outcome == ConnectionProbeOutcome.TimedOut)
        {
            return Error(ConnectionRuntimeErrorCode.Timeout);
        }

        if (result.Outcome == ConnectionProbeOutcome.StartFailed)
        {
            return Error(result.StartFailure switch
            {
                ConnectionProbeStartFailure.NotFound => ConnectionRuntimeErrorCode.RuntimeMissing,
                ConnectionProbeStartFailure.PermissionDenied => ConnectionRuntimeErrorCode.PermissionDenied,
                _ => ConnectionRuntimeErrorCode.ProcessFailed,
            });
        }

        return ConnectionRuntimeError.ClassifyProcessFailure(kind, result.StandardError);
    }

    private static ConnectionRuntimeError Error(ConnectionRuntimeErrorCode code) =>
        ConnectionRuntimeError.Create(code);
}

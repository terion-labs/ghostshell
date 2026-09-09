using Asura.Application;

namespace Asura.App.ViewModels;

public sealed record ConnectionDiagnosticItemViewModel(
    string Stage,
    string Status,
    string Message,
    string StatusColor)
{
    public static ConnectionDiagnosticItemViewModel From(ConnectionDiagnosticItem item) => new(
        item.Stage.ToString(),
        item.Status.ToString(),
        item.Message,
        item.Status switch
        {
            ConnectionDiagnosticStatus.Passed => "#72B57B",
            ConnectionDiagnosticStatus.Warning => "#D79B57",
            ConnectionDiagnosticStatus.Failed => "#FF8577",
            ConnectionDiagnosticStatus.NotRun => "#7B7B82",
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Status, null),
        });
}

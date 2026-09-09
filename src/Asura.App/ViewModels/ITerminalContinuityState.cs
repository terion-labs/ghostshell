namespace Asura.App.ViewModels;

/// <summary>
/// The typed header contract for terminal continuity presentation.
/// </summary>
public interface ITerminalContinuityState
{
    bool IsContinuityActive { get; }
}

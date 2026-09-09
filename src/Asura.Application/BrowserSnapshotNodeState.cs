namespace Asura.Application;

[Flags]
public enum BrowserSnapshotNodeState
{
    None = 0,
    Disabled = 1 << 0,
    Checked = 1 << 1,
    Selected = 1 << 2,
    Expanded = 1 << 3,
    Pressed = 1 << 4,
    Required = 1 << 5,
    ReadOnly = 1 << 6,
}

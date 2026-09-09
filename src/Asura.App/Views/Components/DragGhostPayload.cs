using FluentIcons.Common;

namespace Asura.App.Views.Components;

/// <summary>
/// The small, non-interactive summary that follows an in-app data drag.
/// The actual transfer payload stays separate so drop targets never depend on
/// presentation data.
/// </summary>
internal sealed record DragGhostPayload(
    Symbol Symbol,
    string Title,
    string Detail);

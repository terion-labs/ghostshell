using FluentIcons.Common;

namespace Asura.App.ViewModels;

/// <summary>The compile-time view contract rendered by the runtime tab strip.</summary>
public interface IRuntimeTabStripItem
{
    string Title { get; }

    string Icon { get; }

    Symbol IconSymbol { get; }

    bool IsActive { get; }

    bool CanClose { get; }

    bool HasAttention { get; }

    string AgentActivity { get; }

    bool HasAgentActivity { get; }
}

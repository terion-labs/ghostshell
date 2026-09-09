using System.Text.Json.Serialization;

namespace Asura.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorkspaceEntry.ConnectionReference), "connection")]
[JsonDerivedType(typeof(WorkspaceEntry.ScreenReference), "screen")]
[JsonDerivedType(typeof(WorkspaceEntry.Tab), "tab")]
public abstract record WorkspaceEntry
{
    private protected WorkspaceEntry(WorkspaceEntryId id)
    {
        Id = id;
    }

    public WorkspaceEntryId Id { get; }

    public sealed record ConnectionReference : WorkspaceEntry
    {
        [JsonConstructor]
        public ConnectionReference(WorkspaceEntryId id, ConnectionId connectionId, string? alias = null)
            : base(id)
        {
            ConnectionId = connectionId;
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        }

        public ConnectionId ConnectionId { get; }

        public string? Alias { get; }
    }

    public sealed record ScreenReference : WorkspaceEntry
    {
        [JsonConstructor]
        public ScreenReference(WorkspaceEntryId id, ScreenId screenId, string? alias = null)
            : base(id)
        {
            ScreenId = screenId;
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        }

        public ScreenId ScreenId { get; }

        public string? Alias { get; }
    }

    public sealed record Tab : WorkspaceEntry
    {
        [JsonConstructor]
        public Tab(
            WorkspaceEntryId id,
            string name,
            LayoutId layoutId,
            IReadOnlyList<ScreenPanelDefinition> panels)
            : base(id)
        {
            Name = name;
            LayoutId = layoutId;
            Panels = Array.AsReadOnly(panels?.ToArray() ?? throw new ArgumentNullException(nameof(panels)));
        }

        public string Name { get; }

        public LayoutId LayoutId { get; }

        public IReadOnlyList<ScreenPanelDefinition> Panels { get; }
    }
}

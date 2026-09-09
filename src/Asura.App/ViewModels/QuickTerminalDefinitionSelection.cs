using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

internal sealed record QuickTerminalDefinitionSelection(
    StoredDefinition<ConnectionProfile>? Connection,
    StoredDefinition<TerminalProfile>? TerminalProfile,
    StoredDefinition<KeymapProfile>? TerminalKeymap)
{
    private static readonly ConnectionId BuiltInLocalConnectionId = new("builtin.local");
    private static readonly TerminalProfileId BuiltInTerminalProfileId = new("builtin.terminal.default");

    public QuickTerminalDefinitionSignature Signature => new(
        Connection?.Value.Id,
        Connection?.Revision,
        TerminalProfile?.Value.Id,
        TerminalProfile?.Revision,
        TerminalKeymap?.Value.Id,
        TerminalKeymap?.Revision);

    public static QuickTerminalDefinitionSelection Resolve(DefinitionCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var connection = snapshot.Connections
            .Where(item => item.Value.Endpoint is ConnectionEndpoint.Local)
            .OrderByDescending(item => item.Value.Id == BuiltInLocalConnectionId)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        var terminalProfile = snapshot.TerminalProfiles
            .OrderByDescending(item => item.Value.Id == BuiltInTerminalProfileId)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        var terminalKeymap = terminalProfile is null
            ? null
            : ResolveKeymap(snapshot, terminalProfile.Value.KeymapId);
        return new QuickTerminalDefinitionSelection(connection, terminalProfile, terminalKeymap);
    }

    private static StoredDefinition<KeymapProfile>? ResolveKeymap(
        DefinitionCatalogSnapshot snapshot,
        KeymapProfileId keymapId)
    {
        var stored = snapshot.Keymaps.FirstOrDefault(item => item.Value.Id == keymapId);
        if (stored is not null)
        {
            return stored.Value.Layer == KeymapLayer.Terminal ? stored : null;
        }

        var builtIn = BuiltInKeymaps.All.FirstOrDefault(item => item.Id == keymapId);
        return builtIn?.Layer == KeymapLayer.Terminal
            ? new StoredDefinition<KeymapProfile>(
                builtIn,
                Revision: 0,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch)
            : null;
    }
}

internal readonly record struct QuickTerminalDefinitionSignature(
    ConnectionId? ConnectionId,
    long? ConnectionRevision,
    TerminalProfileId? TerminalProfileId,
    long? TerminalProfileRevision,
    KeymapProfileId? TerminalKeymapId,
    long? TerminalKeymapRevision);

internal sealed class QuickTerminalDefinitionTracker
{
    private QuickTerminalDefinitionSelection _current;

    public QuickTerminalDefinitionTracker(DefinitionCatalogSnapshot initialSnapshot)
    {
        _current = QuickTerminalDefinitionSelection.Resolve(initialSnapshot);
    }

    public bool LastChangeRequiresSessionReset { get; private set; }

    public TerminalRenderProfileSnapshot? CurrentRenderProfile =>
        _current.TerminalProfile is { } profile
            ? TerminalRenderProfileSnapshot.FromProfile(profile.Value)
            : null;

    public bool Update(DefinitionCatalogSnapshot snapshot)
    {
        var next = QuickTerminalDefinitionSelection.Resolve(snapshot);
        if (next.Signature == _current.Signature)
        {
            LastChangeRequiresSessionReset = false;
            return false;
        }

        LastChangeRequiresSessionReset =
            next.Connection?.Value.Id != _current.Connection?.Value.Id
            || next.Connection?.Revision != _current.Connection?.Revision
            || next.TerminalProfile?.Value.Id != _current.TerminalProfile?.Value.Id
            || !HasSameSessionPolicy(
                _current.TerminalProfile?.Value,
                next.TerminalProfile?.Value)
            || next.TerminalKeymap?.Value.Id != _current.TerminalKeymap?.Value.Id
            || next.TerminalKeymap?.Revision != _current.TerminalKeymap?.Revision;
        _current = next;
        return true;
    }

    private static bool HasSameSessionPolicy(
        TerminalProfile? previous,
        TerminalProfile? next)
    {
        if (previous is null || next is null)
        {
            return previous is null && next is null;
        }

        return previous.ScrollbackLines == next.ScrollbackLines
            && Equals(previous.ClipboardPolicy, next.ClipboardPolicy)
            && previous.LinkPolicy == next.LinkPolicy
            && previous.ImeEnabled == next.ImeEnabled
            && previous.ShellIntegration == next.ShellIntegration
            && previous.BellMode == next.BellMode
            && previous.Compatibility == next.Compatibility;
    }
}

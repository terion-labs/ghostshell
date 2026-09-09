using System.Text.Json.Serialization;

namespace Asura.Core;

public sealed class PanelInstance
{
    [JsonConstructor]
    public PanelInstance(
        PanelInstanceId id,
        PanelKind kind,
        string title,
        SessionId? sessionId = null)
    {
        RuntimeInstanceValidation.RequireId(id.Value, nameof(id));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (sessionId is { } activeSessionId)
        {
            RuntimeInstanceValidation.RequireId(activeSessionId.Value, nameof(sessionId));
        }

        Id = id;
        Kind = kind;
        Title = RuntimeInstanceValidation.RequireTitle(title, nameof(title));
        SessionId = sessionId;
    }

    public PanelInstance(PanelInstance source)
        : this(
            (source ?? throw new ArgumentNullException(nameof(source))).Id,
            source.Kind,
            source.Title,
            source.SessionId)
    {
    }

    public PanelInstanceId Id { get; }

    public PanelKind Kind { get; }

    public string Title { get; }

    public SessionId? SessionId { get; }

    public PanelInstance WithSession(SessionId? sessionId) =>
        SessionId == sessionId
            ? this
            : new PanelInstance(Id, Kind, Title, sessionId);
}

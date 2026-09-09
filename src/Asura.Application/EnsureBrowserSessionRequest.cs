using Asura.Core;

namespace Asura.Application;

public sealed record EnsureBrowserSessionRequest(
    SessionId SessionId,
    SessionOwner Owner,
    string Title,
    BrowserAddress InitialAddress);

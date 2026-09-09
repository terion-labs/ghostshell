using Asura.Core;

namespace Asura.Application;

public sealed record BrowserNavigateRequest(
    SessionId SessionId,
    BrowserAddress Address);

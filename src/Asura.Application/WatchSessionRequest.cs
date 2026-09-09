using Asura.Core;

namespace Asura.Application;

public sealed record WatchSessionRequest(SessionId SessionId, long AfterSequence);

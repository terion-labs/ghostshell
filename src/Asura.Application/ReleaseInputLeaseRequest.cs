using Asura.Core;

namespace Asura.Application;

public sealed record ReleaseInputLeaseRequest(SessionId SessionId, InputLeaseId LeaseId);

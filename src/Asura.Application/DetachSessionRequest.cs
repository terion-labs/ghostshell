using Asura.Core;

namespace Asura.Application;

public sealed record DetachSessionRequest(AttachmentId AttachmentId, SessionId SessionId);

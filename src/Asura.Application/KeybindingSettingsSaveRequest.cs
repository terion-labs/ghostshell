using Asura.Core;

namespace Asura.Application;

public sealed record KeybindingSettingsSaveRequest(
    KeymapProfile Profile,
    long? ExpectedRevision);

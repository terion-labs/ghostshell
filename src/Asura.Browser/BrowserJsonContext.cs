using System.Text.Json.Serialization;

namespace Asura.Browser;

internal sealed record CdpInsertTextParameters(string Text);

internal sealed record CdpMouseEventParameters(
    string Type,
    double X,
    double Y,
    string Button,
    int Buttons,
    int Modifiers,
    int ClickCount,
    double DeltaX,
    double DeltaY,
    string PointerType);

internal sealed record CdpCreateIsolatedWorldParameters(
    string FrameId,
    string WorldName,
    bool GrantUniveralAccess);

internal sealed record CdpEvaluationParameters(
    string Expression,
    bool AwaitPromise,
    bool ReturnByValue,
    bool GeneratePreview,
    bool UserGesture,
    double Timeout,
    bool DisableBreaks,
    bool ReplMode,
    bool AllowUnsafeEvalBlockedByCSP,
    bool ThrowOnSideEffect,
    int? ContextId);

internal sealed record CdpAutomationKeyEventParameters(
    string Type,
    int Modifiers,
    string Key,
    string Code,
    string Text,
    string UnmodifiedText,
    int WindowsVirtualKeyCode,
    int NativeVirtualKeyCode,
    bool AutoRepeat,
    bool IsKeypad,
    bool IsSystemKey);

internal sealed record CdpSemanticKeyEventParameters(
    string Type,
    string Key,
    string Code,
    string Text,
    string UnmodifiedText,
    int WindowsVirtualKeyCode,
    int NativeVirtualKeyCode,
    IReadOnlyList<string>? Commands);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CdpInsertTextParameters))]
[JsonSerializable(typeof(CdpMouseEventParameters))]
[JsonSerializable(typeof(CdpCreateIsolatedWorldParameters))]
[JsonSerializable(typeof(CdpEvaluationParameters))]
[JsonSerializable(typeof(CdpAutomationKeyEventParameters))]
[JsonSerializable(typeof(CdpSemanticKeyEventParameters))]
internal sealed partial class BrowserJsonContext : JsonSerializerContext;

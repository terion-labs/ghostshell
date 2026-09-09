namespace Asura.Application;

/// <summary>
/// The closed, non-identifying metadata shape accepted by diagnostics export. Machine names, user
/// names, process arguments, environment values, and connection details have no representation here.
/// </summary>
public sealed record DiagnosticsBundleMetadata(
    string ApplicationName,
    string ApplicationIdentifier,
    string ExecutableName,
    string ApplicationVersion,
    string RuntimeVersion,
    string OperatingSystem,
    string Architecture,
    DateTimeOffset CapturedAt);

using Asura.Application;

namespace Asura.App.ViewModels;

/// <summary>
/// The statically typed data contract consumed by the reusable SQL editor.
/// The surrounding database workspace owns a much larger presentation model,
/// but the editor must not discover those four members at runtime.
/// </summary>
public interface ISqlQueryEditorHost
{
    SqlCompletionContext SqlLanguageCompletionContext { get; }

    ISqlLanguageSession? SqlLanguageSession { get; }

    string SqlLanguageStatus { get; }

    string QueryText { get; set; }
}

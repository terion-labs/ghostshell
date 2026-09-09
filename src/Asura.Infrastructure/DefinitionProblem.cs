using Asura.Core;

namespace Asura.Infrastructure;

internal enum DefinitionProblemKind
{
    InvalidDefinition,
    UnsupportedKind,
    UnsupportedSchema,
    UnsafePayload,
    MissingDependency,
    DependencyConflict,
    StorageFailure,
}

internal sealed record DefinitionProblem(
    DefinitionProblemKind Kind,
    string Message,
    DefinitionKey? Definition = null);

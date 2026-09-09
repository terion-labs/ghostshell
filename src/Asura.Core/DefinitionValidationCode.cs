namespace Asura.Core;

public enum DefinitionValidationCode
{
    Required = 1,
    InvalidSchemaVersion = 2,
    DuplicateId = 3,
    InvalidGrid = 4,
    InvalidBounds = 5,
    OutOfBounds = 6,
    Overlap = 7,
    InvalidMinimumSize = 8,
    CanvasTooSmall = 9,
    LayoutMismatch = 10,
    UnknownSlot = 11,
    MissingSlot = 12,
    InvalidPanel = 13,
    InvalidEntry = 14,
    MissingDependency = 15,
    InvalidAgentPolicy = 16,
}

namespace Asura.Application;

public enum ConnectionProgressStage
{
    ValidatingProfile,
    DetectingRuntime,
    ResolvingCredentials,
    BuildingLaunchPlan,
    InspectingHostKey,
    Authenticating,
    ProbingEndpoint,
    Reconnecting,
    Completed,
}

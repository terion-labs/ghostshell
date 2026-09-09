namespace Asura.Core;

/// <summary>
/// Provider-native request scheduling. Support is model and route specific;
/// adapters reject any value not advertised for their exact binding.
/// </summary>
public enum AgentServiceTier
{
    Automatic,
    Default,
    Flex,
    Priority,
}

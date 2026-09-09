using Asura.Application.ApplicationUpdates;

namespace Asura.Updates;

public static class InstalledApplicationUpdates
{
    public static IApplicationUpdateService Create(Action requestShutdown)
    {
        ArgumentNullException.ThrowIfNull(requestShutdown);
        var distribution = DistributionIdentityReader.ReadInstalled();
        return distribution.UpdateStrategy == ApplicationUpdateStrategy.Velopack
            ? new VelopackApplicationUpdateService(distribution, requestShutdown)
            : new PassiveApplicationUpdateService(distribution);
    }
}

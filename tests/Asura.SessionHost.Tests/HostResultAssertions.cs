using Asura.Application;

namespace Asura.SessionHost.Tests;

internal static class HostResultAssertions
{
    public static T Value<T>(this HostResult<T> result)
    {
        var success = Assert.IsType<HostResult<T>.Success>(result);
        return success.Value;
    }

    public static HostError Error<T>(this HostResult<T> result)
    {
        var failure = Assert.IsType<HostResult<T>.Failure>(result);
        return failure.Error;
    }
}

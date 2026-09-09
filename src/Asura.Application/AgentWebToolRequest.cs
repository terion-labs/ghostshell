using System.Text;

namespace Asura.Application;

public abstract record AgentWebToolRequest
{
    public const int MaximumUrlBytes = 2_048;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private protected AgentWebToolRequest()
    {
    }

    public abstract string ToolName { get; }

    internal static Uri RequireHttpAddress(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!TryGetByteCount(value, out var byteCount)
            || byteCount > MaximumUrlBytes
            || !Uri.TryCreate(value, UriKind.Absolute, out var address)
            || !(address.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || address.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(address.UserInfo)
            || !string.IsNullOrEmpty(address.Fragment))
        {
            throw new ArgumentException(
                "The address must be a bounded, credential-free HTTP(S) URL without a fragment.",
                parameterName);
        }

        return address;
    }

    private static bool TryGetByteCount(string value, out int byteCount)
    {
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
    }
}

public enum AgentHttpFetchMethod
{
    Get,
    Head,
}

public sealed record AgentHttpFetchRequest : AgentWebToolRequest
{
    public AgentHttpFetchRequest(
        string url,
        AgentHttpFetchMethod method = AgentHttpFetchMethod.Get)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        Address = RequireHttpAddress(url, nameof(url));
        Method = method;
    }

    public override string ToolName => BuiltInAgentTools.HttpFetch;

    public Uri Address { get; }

    public AgentHttpFetchMethod Method { get; }
}

public enum AgentWebReadFormat
{
    Markdown,
    RenderedHtml,
}

public sealed record AgentWebReadRequest : AgentWebToolRequest
{
    public AgentWebReadRequest(
        string url,
        AgentWebReadFormat format = AgentWebReadFormat.Markdown)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        Address = RequireHttpAddress(url, nameof(url));
        Format = format;
    }

    public override string ToolName => BuiltInAgentTools.WebRead;

    public Uri Address { get; }

    public AgentWebReadFormat Format { get; }
}

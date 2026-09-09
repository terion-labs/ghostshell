using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// A request for one bounded numeric snapshot from an exact Statistics panel.
/// </summary>
public sealed record AgentStatisticsReadRequest
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentStatisticsReadRequest(PanelInstanceId panelId)
    {
        var value = panelId.Value;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An agent statistics request requires a printable panel identifier.",
                nameof(panelId));
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > 256)
            {
                throw new ArgumentException(
                    "An agent statistics request panel identifier is too long.",
                    nameof(panelId));
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "An agent statistics request panel identifier must contain valid Unicode.",
                nameof(panelId),
                exception);
        }

        PanelId = panelId;
    }

    public PanelInstanceId PanelId { get; }
}

using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// A bounded request for the command-line-free process projection exposed by
/// one exact local Process Monitor panel.
/// </summary>
public sealed record AgentProcessListRequest
{
    public const int MinimumLimit = 16;
    public const int DefaultLimit = 32;
    public const int MaximumLimit = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentProcessListRequest(
        PanelInstanceId panelId,
        int limit = DefaultLimit,
        ProcessMonitorSort sort = ProcessMonitorSort.CpuDescending,
        int offset = 0,
        string? nameContains = null,
        int? processId = null)
    {
        RequirePanelId(panelId);
        if (limit is not (MinimumLimit or DefaultLimit or MaximumLimit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "An agent process list limit must be 16, 32, or 64.");
        }

        if (sort is not (
            ProcessMonitorSort.CpuDescending
            or ProcessMonitorSort.MemoryDescending
            or ProcessMonitorSort.NameAscending
            or ProcessMonitorSort.ProcessIdAscending))
        {
            throw new ArgumentOutOfRangeException(nameof(sort));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, 1_000_000);
        if (nameContains is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nameContains);
            if (nameContains.Length > 128 || nameContains.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "A process-name filter must be bounded printable text.",
                    nameof(nameContains));
            }
        }

        if (processId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        PanelId = panelId;
        Limit = limit;
        Sort = sort;
        Offset = offset;
        NameContains = nameContains is null ? null : string.Concat(nameContains);
        ProcessId = processId;
    }

    public PanelInstanceId PanelId { get; }

    public int Limit { get; }

    public ProcessMonitorSort Sort { get; }

    public int Offset { get; }

    public string? NameContains { get; }

    public int? ProcessId { get; }

    private static void RequirePanelId(PanelInstanceId panelId)
    {
        var value = panelId.Value;
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An agent process request requires a printable panel identifier.",
                nameof(panelId));
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > 256)
            {
                throw new ArgumentException(
                    "An agent process request panel identifier is too long.",
                    nameof(panelId));
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "An agent process request panel identifier must contain valid Unicode.",
                nameof(panelId),
                exception);
        }
    }
}

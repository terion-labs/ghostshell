using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal sealed record ProcessAgentIntent
{
    public ProcessAgentIntent(
        int limit,
        ProcessMonitorSort sort,
        int offset = 0,
        string? nameContains = null,
        int? processId = null)
    {
        if (!ProcessAgentToolSet.IsAllowedLimit(limit))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, 1_000_000);

        if (sort is not (
            ProcessMonitorSort.CpuDescending
            or ProcessMonitorSort.MemoryDescending
            or ProcessMonitorSort.NameAscending
            or ProcessMonitorSort.ProcessIdAscending))
        {
            throw new ArgumentOutOfRangeException(nameof(sort));
        }

        Limit = limit;
        Sort = sort;
        Offset = offset;
        NameContains = nameContains;
        ProcessId = processId;
    }

    public int Limit { get; }

    public ProcessMonitorSort Sort { get; }

    public int Offset { get; }

    public string? NameContains { get; }

    public int? ProcessId { get; }

    public string SortName =>
        Sort switch
        {
            ProcessMonitorSort.CpuDescending => "cpu_desc",
            ProcessMonitorSort.MemoryDescending => "memory_desc",
            ProcessMonitorSort.NameAscending => "name_asc",
            ProcessMonitorSort.ProcessIdAscending => "pid_asc",
            _ => throw new ArgumentOutOfRangeException(
                nameof(Sort),
                Sort,
                "The process sort is unsupported."),
        };
}

internal abstract record ProcessAgentIntentResult
{
    private ProcessAgentIntentResult()
    {
    }

    public sealed record Parsed(
        ProcessAgentIntent Intent,
        PanelInstanceId PanelId)
        : ProcessAgentIntentResult;

    public sealed record Rejected(
        string StableCode,
        string Message)
        : ProcessAgentIntentResult;
}

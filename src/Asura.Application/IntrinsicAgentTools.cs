namespace Asura.Application;

/// <summary>
/// Run-local model tools handled entirely by the governed agent runtime. These
/// names deliberately sit outside the broker capability catalog.
/// </summary>
public static class IntrinsicAgentTools
{
    public const string AskUser = "agent.ask_user";

    public const string RequestCapability = "agent.request_capability";

    public const string ReportProgress = "agent.report_progress";

    public const string RunSequence = "agent.run_sequence";
}

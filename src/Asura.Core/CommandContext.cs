namespace Asura.Core;

[Flags]
public enum CommandContext
{
    None = 0,
    Global = 1 << 0,
    Window = 1 << 1,
    Workspace = 1 << 2,
    Tab = 1 << 3,
    Panel = 1 << 4,
    Terminal = 1 << 5,
    Browser = 1 << 6,
    TextEditing = 1 << 7,
    QuickTerminal = 1 << 8,
    Modal = 1 << 9,
    All = Global | Window | Workspace | Tab | Panel | Terminal | Browser | TextEditing | QuickTerminal | Modal,
}

internal static class CommandContextRules
{
    public static CommandContext Require(CommandContext contexts, string parameterName)
    {
        if (contexts == CommandContext.None || (contexts & ~CommandContext.All) != CommandContext.None)
        {
            throw new ArgumentOutOfRangeException(parameterName, contexts, "At least one known command context is required.");
        }

        return contexts;
    }

    public static IEnumerable<CommandContext> Enumerate(CommandContext contexts)
    {
        foreach (var context in Enum.GetValues<CommandContext>())
        {
            if (context is CommandContext.None or CommandContext.All)
            {
                continue;
            }

            if ((contexts & context) != CommandContext.None)
            {
                yield return context;
            }
        }
    }

    public static bool CanBeActiveTogether(CommandContext left, CommandContext right)
    {
        if (left == right || left is CommandContext.Global or CommandContext.Modal
            || right is CommandContext.Global or CommandContext.Modal)
        {
            return true;
        }

        return (left, right) is not (CommandContext.Terminal, CommandContext.Browser)
            and not (CommandContext.Browser, CommandContext.Terminal);
    }

    public static int ResolutionPriority(CommandContext context) => context switch
    {
        CommandContext.Modal => 100,
        CommandContext.QuickTerminal => 90,
        CommandContext.Global => 80,
        CommandContext.Window => 70,
        CommandContext.Workspace => 60,
        CommandContext.Tab => 50,
        CommandContext.Panel => 40,
        CommandContext.Terminal or CommandContext.Browser => 30,
        CommandContext.TextEditing => 20,
        _ => throw new ArgumentOutOfRangeException(nameof(context), context, null),
    };
}

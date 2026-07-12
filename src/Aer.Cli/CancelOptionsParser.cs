namespace Aer.Cli;

/// <summary>
/// Parses <c>aer cancel</c>'s arguments: <c>aer cancel &lt;task-dir&gt; --execution &lt;execution-id&gt;
/// --bindings &lt;bindings-file&gt; [--workflow-id &lt;id&gt;]</c>. Never throws a bare
/// <see cref="InvalidOperationException"/> for a malformed invocation — every failure here is a
/// <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules), mirroring
/// <see cref="RunOptionsParser"/>.
/// </summary>
public static class CancelOptionsParser
{
    private const string Usage =
        "Usage: aer cancel <task-dir> --execution <execution-id> --bindings <bindings-file> [--workflow-id <id>]";

    public static CancelOptions Parse(IReadOnlyList<string> args)
    {
        string? taskDirectoryPath = null;
        string? executionId = null;
        string? bindingsFilePath = null;
        string? workflowId = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--execution":
                    executionId = RequireValue(args, ref i, arg);
                    break;
                case "--bindings":
                    bindingsFilePath = RequireValue(args, ref i, arg);
                    break;
                case "--workflow-id":
                    workflowId = RequireValue(args, ref i, arg);
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (taskDirectoryPath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    taskDirectoryPath = arg;
                    i++;
                    break;
            }
        }

        if (taskDirectoryPath is null)
        {
            throw new CliArgumentException($"Missing required <task-dir> argument. {Usage}");
        }

        if (executionId is null)
        {
            throw new CliArgumentException($"Missing required option '--execution <execution-id>'. {Usage}");
        }

        if (bindingsFilePath is null)
        {
            throw new CliArgumentException($"Missing required option '--bindings <bindings-file>'. {Usage}");
        }

        return new CancelOptions(taskDirectoryPath, executionId, bindingsFilePath, workflowId);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new CliArgumentException($"Option '{optionName}' requires a value. {Usage}");
        }

        var value = args[index + 1];
        index += 2;
        return value;
    }
}

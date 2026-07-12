namespace Aer.Cli;

/// <summary>
/// Parses <c>aer run</c>'s arguments: <c>aer run &lt;workflow-file&gt; --bindings &lt;bindings-file&gt;
/// [--task-dir &lt;dir&gt;] [--workflow-id &lt;id&gt;]</c>. Never throws a bare
/// <see cref="InvalidOperationException"/> for a malformed invocation — every failure here is a
/// <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules).
/// </summary>
public static class RunOptionsParser
{
    private const string Usage =
        "Usage: aer run <workflow-file> --bindings <bindings-file> [--task-dir <dir>] [--workflow-id <id>]";

    public static RunOptions Parse(IReadOnlyList<string> args)
    {
        string? workflowFilePath = null;
        string? bindingsFilePath = null;
        string? taskDirectoryPath = null;
        string? workflowId = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--bindings":
                    bindingsFilePath = RequireValue(args, ref i, arg);
                    break;
                case "--task-dir":
                    taskDirectoryPath = RequireValue(args, ref i, arg);
                    break;
                case "--workflow-id":
                    workflowId = RequireValue(args, ref i, arg);
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (workflowFilePath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    workflowFilePath = arg;
                    i++;
                    break;
            }
        }

        if (workflowFilePath is null)
        {
            throw new CliArgumentException($"Missing required <workflow-file> argument. {Usage}");
        }

        if (bindingsFilePath is null)
        {
            throw new CliArgumentException($"Missing required option '--bindings <bindings-file>'. {Usage}");
        }

        // Derived from the workflow file's own name when not given, so `aer run workflow.json`
        // twice in the same directory naturally resumes the same task (§21) rather than each
        // invocation needing its own explicit --task-dir.
        taskDirectoryPath ??= Path.Combine(
            Directory.GetCurrentDirectory(), ".aer", Path.GetFileNameWithoutExtension(workflowFilePath));

        return new RunOptions(workflowFilePath, bindingsFilePath, taskDirectoryPath, workflowId);
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

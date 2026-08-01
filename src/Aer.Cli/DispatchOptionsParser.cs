namespace Aer.Cli;

/// <summary>
/// Parses <c>aer dispatch</c>'s arguments: <c>aer dispatch &lt;role&gt; --spec &lt;spec-file&gt;
/// [--adapter &lt;name&gt;] [--task-dir &lt;dir&gt;] [--workflow-id &lt;id&gt;]</c>. Every malformed
/// invocation is a <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules), never a bare
/// <see cref="InvalidOperationException"/>.
/// </summary>
public static class DispatchOptionsParser
{
    /// <summary>The one copy of <c>aer dispatch</c>'s usage line, printed here on error and by <c>Program</c>.</summary>
    public const string Usage =
        "Usage: aer dispatch <role> --spec <spec-file> [--adapter <name>] [--task-dir <dir>] [--workflow-id <id>]";

    public static DispatchOptions Parse(IReadOnlyList<string> args)
    {
        string? roleId = null;
        string? specFilePath = null;
        string? adapter = null;
        string? taskDirectoryPath = null;
        string? workflowId = null;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--spec":
                    specFilePath = RequireValue(args, ref i, arg);
                    break;
                case "--adapter":
                    adapter = RequireValue(args, ref i, arg);
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

                    if (roleId is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    roleId = arg;
                    i++;
                    break;
            }
        }

        if (roleId is null)
        {
            throw new CliArgumentException($"Missing required <role> argument. {Usage}");
        }

        if (specFilePath is null)
        {
            throw new CliArgumentException($"Missing required option '--spec <spec-file>'. {Usage}");
        }

        // Fresh and unique per invocation unless pinned: a dispatch is one-shot, and deriving a stable
        // directory from the role (the way `aer run` derives one from the workflow file) would make a
        // second `aer dispatch review` resume — and so replay — the first's terminal snapshot rather
        // than run again. The per-execution artifact dir already keeps outputs collision-free (#897);
        // this keeps the *task* fresh so the orchestrator's repeated self-dispatch (#778) actually reruns.
        if (taskDirectoryPath is null)
        {
            var uniqueName = $"dispatch-{roleId}-{Guid.NewGuid().ToString("N")[..8]}";
            taskDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), ".aer", uniqueName);
        }

        return new DispatchOptions(
            roleId, specFilePath, TaskDirectoryPath.Resolve(taskDirectoryPath), adapter, workflowId);
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

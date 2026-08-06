namespace Aer.Cli;

/// <summary>
/// Parses <c>aer status</c>'s arguments: <c>aer status &lt;task-dir&gt; [--follow]</c>. Never
/// throws a bare <see cref="InvalidOperationException"/> for a malformed invocation — every
/// failure here is a <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules),
/// mirroring <see cref="RunOptionsParser"/>/<see cref="CancelOptionsParser"/>.
/// </summary>
public static class StatusOptionsParser
{
    public const string Usage = "Usage: aer status <task-dir> [--follow]";

    public static StatusOptions Parse(IReadOnlyList<string> args)
    {
        string? roomDirectoryPath = null;
        var follow = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--follow":
                    follow = true;
                    i++;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new CliArgumentException($"Unknown option '{arg}'. {Usage}");
                    }

                    if (roomDirectoryPath is not null)
                    {
                        throw new CliArgumentException($"Unexpected extra argument '{arg}'. {Usage}");
                    }

                    roomDirectoryPath = arg;
                    i++;
                    break;
            }
        }

        if (roomDirectoryPath is null)
        {
            throw new CliArgumentException($"Missing required <task-dir> argument. {Usage}");
        }

        return new StatusOptions(RoomDirectoryPath.Resolve(roomDirectoryPath), follow);
    }
}

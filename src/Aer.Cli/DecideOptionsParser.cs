using Aer.Flow.Domain;

namespace Aer.Cli;

/// <summary>
/// Parses <c>aer decide</c>'s arguments: <c>aer decide &lt;task-dir&gt; --execution &lt;execution-id&gt;
/// --type resume|reject|retry-with-revision|supersede [--target-step &lt;step-id&gt;]
/// [--supplementary &lt;execution-id&gt;] --bindings &lt;bindings-file&gt; [--workflow-id &lt;id&gt;]</c>.
/// Never throws a bare <see cref="InvalidOperationException"/> for a malformed invocation — every
/// failure here is a <see cref="CliArgumentException"/> (CLAUDE.md's error-handling rules),
/// mirroring <see cref="CancelOptionsParser"/>. Every validity rule beyond "is this one of §17.2's
/// four spellings" stays <c>ExternalDecisionValidator</c>'s (e.g. whether <c>--target-step</c> is
/// required for the given <c>--type</c>) — this parser adds no vocabulary of its own.
/// </summary>
public static class DecideOptionsParser
{
    private const string Usage =
        "Usage: aer decide <task-dir> --execution <execution-id> --type resume|reject|retry-with-revision|supersede " +
        "[--target-step <step-id>] [--supplementary <execution-id>] --bindings <bindings-file> [--workflow-id <id>]";

    public static DecideOptions Parse(IReadOnlyList<string> args)
    {
        string? taskDirectoryPath = null;
        string? executionId = null;
        string? typeText = null;
        string? targetStep = null;
        string? supplementaryExecutionId = null;
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
                case "--type":
                    typeText = RequireValue(args, ref i, arg);
                    break;
                case "--target-step":
                    targetStep = RequireValue(args, ref i, arg);
                    break;
                case "--supplementary":
                    supplementaryExecutionId = RequireValue(args, ref i, arg);
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

        if (typeText is null)
        {
            throw new CliArgumentException($"Missing required option '--type <decision-type>'. {Usage}");
        }

        if (bindingsFilePath is null)
        {
            throw new CliArgumentException($"Missing required option '--bindings <bindings-file>'. {Usage}");
        }

        var decisionType = ParseDecisionType(typeText);

        return new DecideOptions(
            taskDirectoryPath,
            executionId,
            decisionType,
            targetStep is null ? null : new StepId(targetStep),
            supplementaryExecutionId,
            bindingsFilePath,
            workflowId);
    }

    private static DecisionType ParseDecisionType(string typeText) => typeText switch
    {
        "resume" => DecisionType.Resume,
        "reject" => DecisionType.Reject,
        "retry-with-revision" => DecisionType.RetryWithRevision,
        "supersede" => DecisionType.Supersede,
        _ => throw new CliArgumentException(
            $"Unknown decision type '{typeText}'. Must be one of: resume, reject, retry-with-revision, supersede. {Usage}"),
    };

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

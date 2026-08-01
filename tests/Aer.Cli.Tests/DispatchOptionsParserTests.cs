namespace Aer.Cli.Tests;

/// <summary>
/// <c>aer dispatch</c>'s argument parsing (#900): the role is positional, <c>--spec</c> is required,
/// and every malformed invocation is a typed <see cref="CliArgumentException"/> rather than a bare
/// throw (CLAUDE.md's error-handling rules).
/// </summary>
public class DispatchOptionsParserTests
{
    [Fact]
    public void Parses_the_role_spec_adapter_task_dir_and_workflow_id()
    {
        var options = DispatchOptionsParser.Parse(
            ["review", "--spec", "task.md", "--adapter", "gemini", "--task-dir", "out", "--workflow-id", "wf"]);

        Assert.Equal("review", options.RoleId);
        Assert.Equal("task.md", options.SpecFilePath);
        Assert.Equal("gemini", options.Adapter);
        Assert.Equal("wf", options.WorkflowId);
        Assert.EndsWith("out", options.TaskDirectoryPath);
    }

    [Fact]
    public void A_missing_role_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["--spec", "task.md"]));
        Assert.Contains("<role>", ex.Message);
    }

    [Fact]
    public void A_missing_spec_is_a_typed_argument_error()
    {
        var ex = Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["review"]));
        Assert.Contains("--spec", ex.Message);
    }

    [Fact]
    public void An_unknown_option_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["review", "--spec", "t.md", "--nope", "x"]));
    }

    [Fact]
    public void A_second_positional_argument_is_a_typed_argument_error()
    {
        Assert.Throws<CliArgumentException>(() => DispatchOptionsParser.Parse(["review", "extra", "--spec", "t.md"]));
    }

    [Fact]
    public void The_default_task_directory_is_unique_per_invocation_so_a_redispatch_does_not_resume()
    {
        // A one-shot dispatch must run anew each time; two default directories that collided would
        // make the second invocation resume — and replay — the first's terminal snapshot.
        var first = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]).TaskDirectoryPath;
        var second = DispatchOptionsParser.Parse(["review", "--spec", "t.md"]).TaskDirectoryPath;
        Assert.NotEqual(first, second);
    }
}

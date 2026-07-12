namespace Aer.Cli.Tests;

public class RunOptionsParserTests
{
    [Fact]
    public void A_workflow_file_and_bindings_option_parse_with_a_derived_default_task_directory()
    {
        var options = RunOptionsParser.Parse(["workflow.json", "--bindings", "bindings.json"]);

        Assert.Equal("workflow.json", options.WorkflowFilePath);
        Assert.Equal("bindings.json", options.BindingsFilePath);
        Assert.Equal(
            Path.Combine(Directory.GetCurrentDirectory(), ".aer", "workflow"),
            options.TaskDirectoryPath);
        Assert.Null(options.WorkflowId);
    }

    [Fact]
    public void An_explicit_task_dir_and_workflow_id_override_the_defaults()
    {
        var options = RunOptionsParser.Parse(
            ["workflow.json", "--bindings", "bindings.json", "--task-dir", "/tmp/task", "--workflow-id", "wf-1"]);

        Assert.Equal("/tmp/task", options.TaskDirectoryPath);
        Assert.Equal("wf-1", options.WorkflowId);
    }

    [Fact]
    public void Options_may_precede_the_positional_workflow_file()
    {
        var options = RunOptionsParser.Parse(["--bindings", "bindings.json", "workflow.json"]);

        Assert.Equal("workflow.json", options.WorkflowFilePath);
        Assert.Equal("bindings.json", options.BindingsFilePath);
    }

    [Fact]
    public void A_missing_workflow_file_throws()
    {
        Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["--bindings", "bindings.json"]));
    }

    [Fact]
    public void A_missing_bindings_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["workflow.json"]));
    }

    [Fact]
    public void An_option_missing_its_value_throws()
    {
        Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["workflow.json", "--bindings"]));
    }

    [Fact]
    public void An_unknown_option_throws()
    {
        Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["workflow.json", "--bindings", "b.json", "--nope"]));
    }

    [Fact]
    public void A_second_positional_argument_throws()
    {
        Assert.Throws<CliArgumentException>(() => RunOptionsParser.Parse(["workflow.json", "extra.json", "--bindings", "b.json"]));
    }
}

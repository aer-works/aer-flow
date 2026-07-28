namespace Aer.Cli.Tests;

/// <summary>
/// #649: a worker whose grant withholds writes must still be able to write its declared output. The
/// outbox is AER's own directory, outside the workspace — withholding "modify the workspace" was
/// never meant to withhold "write your report", and that conflation is why every reviewing template
/// grants a workspace write it does not need.
/// </summary>
public class OutboxWriteExemptionTests
{
    private static readonly string Outbox =
        Path.Combine(Path.GetTempPath(), "aer-task", "artifacts", "execution_1");

    private static int Decide(string toolName, string? targetPath, string? outbox = null)
    {
        var payload = Payload(toolName, targetPath is null ? null : new { file_path = targetPath });

        using var stderr = new StringWriter();
        return HookCheckCommand.Execute(new StringReader(payload), stderr, "claude:Edit,Write,NotebookEdit", outbox ?? Outbox);
    }

    [Fact]
    public void A_withheld_write_into_the_outbox_is_allowed()
    {
        // The deliverable. Without this a read-only reviewer cannot produce the artifact it was
        // dispatched to produce, which is what #629 now refuses at bind time.
        Assert.Equal(HookCheckCommand.AllowedExitCode, Decide("Write", Path.Combine(Outbox, "review.md")));
    }

    [Fact]
    public void A_withheld_write_into_the_workspace_is_still_denied()
    {
        // The polarity control, and the one that matters: without it everything above passes on a hook
        // that stopped enforcing writes altogether, which is the whole grant becoming decorative.
        Assert.Equal(
            HookCheckCommand.DeniedExitCode,
            Decide("Write", Path.Combine(Path.GetTempPath(), "repo", "src", "Program.cs")));
    }

    [Fact]
    public void A_traversal_out_of_the_outbox_is_denied()
    {
        Assert.Equal(
            HookCheckCommand.DeniedExitCode,
            Decide("Write", Path.Combine(Outbox, "..", "..", "..", "repo", "src", "Program.cs")));
    }

    [Fact]
    public void A_notebook_edit_targets_its_own_property_name()
    {
        // NotebookEdit carries notebook_path, not file_path. Reading only file_path would deny a
        // legitimate outbox write for a reason that has nothing to do with the grant.
        var payload = Payload("NotebookEdit", new { notebook_path = Path.Combine(Outbox, "n.ipynb") });
        using var stderr = new StringWriter();

        Assert.Equal(
            HookCheckCommand.AllowedExitCode,
            HookCheckCommand.Execute(new StringReader(payload), stderr, "claude:Edit,Write,NotebookEdit", Outbox));
    }

    [Fact]
    public void A_withheld_tool_with_no_path_argument_is_still_denied()
    {
        // Bash is withheld by name and has no target for the exemption to apply to. A hook that
        // allowed on a missing path would turn every withheld non-write tool into an allow.
        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(Payload("Bash", new { command = "rm -rf /" })),
            stderr, "claude:Bash", Outbox);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void With_no_outbox_known_the_exemption_does_not_apply()
    {
        // Fails closed. A hook that cannot tell where the outbox is denies exactly as it did before
        // this exemption existed.
        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(Payload("Write", new { file_path = Path.Combine(Outbox, "review.md") })),
            stderr, "claude:Edit,Write,NotebookEdit", outboxDirectory: null);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void The_exemption_covers_writes_only_not_every_tool_carrying_a_file_path()
    {
        // Read carries a file_path too. Keying the exemption off the field rather than the tool name
        // silently exempted reads inside the outbox from a withheld ReadFiles — a category #649 never
        // claimed. The withheld list here grants writes and withholds reads, which is the shape that
        // separates the two.
        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(Payload("Read", new { file_path = Path.Combine(Outbox, "review.md") })),
            stderr, "claude:Read", Outbox);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
    }

    [Fact]
    public void A_relative_outbox_is_refused_rather_than_resolved_against_the_workers_cwd()
    {
        // Measured on a live run: a relative --task-dir emitted AER_OUTPUT_DIR as
        // `task2\artifacts\execution_<id>`. This process inherits the vendor CLI's cwd, which is the
        // workspace, so resolving it here certified a directory *inside the workspace* as the outbox
        // and allowed the write. The worker's report landed there, AER looked at the real path, found
        // nothing, and failed the contract after paying for the run in full.
        const string relative = @"task2\artifacts\execution_1";

        Assert.False(OutboxPath.IsInsideOutbox(Path.Combine(relative, "review.md"), relative));

        // Control: the same shape rooted, which is what AER actually emits, still resolves.
        var rooted = Path.Combine(Path.GetTempPath(), "aer-task", "artifacts", "execution_1");
        Assert.True(OutboxPath.IsInsideOutbox(Path.Combine(rooted, "review.md"), rooted));
    }

    [Fact]
    public void A_link_planted_inside_the_outbox_cannot_launder_a_workspace_write()
    {
        // Path.GetFullPath normalises `..` textually and never follows a link, so a prefix comparison
        // on its output reports a path *through* a link as inside the outbox while the write lands
        // wherever the link points. Demonstrated on a real directory link rather than argued.
        var root = Directory.CreateTempSubdirectory("aer-outbox-link-").FullName;
        try
        {
            var outbox = Directory.CreateDirectory(Path.Combine(root, "artifacts", "execution_1")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "repo", "src")).FullName;

            var link = Path.Combine(outbox, "escape");
            try
            {
                Directory.CreateSymbolicLink(link, workspace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows needs Developer Mode or elevation to create one. The Linux and macOS CI
                // legs carry this assertion; skipping here beats asserting nothing anywhere.
                return;
            }

            var throughTheLink = Path.Combine(link, "Program.cs");

            Assert.False(OutboxPath.IsInsideOutbox(throughTheLink, outbox));
            // The control: the same outbox, a target that really is inside it. Without this, a
            // resolver that answered false for everything would pass the assertion above.
            Assert.True(OutboxPath.IsInsideOutbox(Path.Combine(outbox, "review.md"), outbox));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Builds a hook payload through the serializer rather than as a raw string literal, so a JSON
    /// brace never has to be escaped against C#'s own interpolation syntax — which is a way to write
    /// a test that passes for the wrong reason.
    /// </summary>
    private static string Payload(string toolName, object? toolInput) =>
        System.Text.Json.JsonSerializer.Serialize(
            toolInput is null ? new { tool_name = toolName } : (object)new { tool_name = toolName, tool_input = toolInput });
}

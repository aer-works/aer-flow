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
        //
        // Scope, because the name suggests more than it proves: this guards
        // IsInsideOutbox(null, ...) == false and nothing else. It passes with or without the
        // tool-name gate, since Bash carries no file_path either way — the gate itself is guarded by
        // The_exemption_covers_writes_only_not_every_tool_carrying_a_file_path.
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

        // And the operator is told which of the two things went wrong. The generic withheld-tool
        // message sends them to their permission grant for a fault that is in their --task-dir.
        using var stderr = new StringWriter();
        var exitCode = HookCheckCommand.Execute(
            new StringReader(Payload("Write", new { file_path = Path.Combine(relative, "review.md") })),
            stderr, "claude:Edit,Write,NotebookEdit", relative);

        Assert.Equal(HookCheckCommand.DeniedExitCode, exitCode);
        Assert.Contains("not an absolute path", stderr.ToString(), StringComparison.Ordinal);

        // Control: the same shape rooted, which is what AER actually emits, still resolves.
        var rooted = Path.Combine(Path.GetTempPath(), "aer-task", "artifacts", "execution_1");
        Assert.True(OutboxPath.IsInsideOutbox(Path.Combine(rooted, "review.md"), rooted));
    }

    [Fact]
    public void A_dangling_link_inside_the_outbox_cannot_launder_a_workspace_write()
    {
        // Directory.Exists and File.Exists both stat THROUGH a link, so a link whose target does not
        // exist yet answers false to both. Resolution keyed on those checks therefore appends the
        // link component unresolved and reports the path as contained. The worker's prompt already
        // tells it to create parent directories as needed, so the write creates the target through
        // the link — a workspace write laundered through the outbox.
        var root = Directory.CreateTempSubdirectory("aer-outbox-dangling-").FullName;
        try
        {
            var outbox = Directory.CreateDirectory(Path.Combine(root, "artifacts", "execution_1")).FullName;
            var neverCreated = Path.Combine(root, "repo", "src");

            var link = Path.Combine(outbox, "escape");
            try
            {
                Directory.CreateSymbolicLink(link, neverCreated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            // The premise, and it is platform-split — measured, not assumed. On POSIX, Exists calls
            // stat, which follows the link, so a dangling one reports false and a resolver keyed on
            // Exists treats it as "not a link". Windows reports the reparse point itself as existing,
            // so the hole never opens there. The assertion is scoped to the platforms where it is the
            // premise; CI's Linux and macOS legs are what actually exercise this case.
            if (!OperatingSystem.IsWindows())
            {
                Assert.False(Directory.Exists(link));
            }

            Assert.False(OutboxPath.IsInsideOutbox(Path.Combine(link, "Program.cs"), outbox));
            Assert.True(OutboxPath.IsInsideOutbox(Path.Combine(outbox, "review.md"), outbox));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_link_partway_along_the_path_cannot_launder_a_workspace_write()
    {
        // The shape OutboxPath's own remarks name as the dangerous one — a link mid-path whose final
        // component is an ordinary file — and which the last-position test cannot exercise.
        var root = Directory.CreateTempSubdirectory("aer-outbox-midlink-").FullName;
        try
        {
            var outbox = Directory.CreateDirectory(Path.Combine(root, "artifacts", "execution_1")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "repo", "src", "deep")).FullName;
            File.WriteAllText(Path.Combine(workspace, "Program.cs"), "// real file");

            var link = Path.Combine(outbox, "hop");
            try
            {
                Directory.CreateSymbolicLink(link, Path.Combine(root, "repo", "src"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            Assert.False(OutboxPath.IsInsideOutbox(Path.Combine(link, "deep", "Program.cs"), outbox));
            Assert.True(OutboxPath.IsInsideOutbox(Path.Combine(outbox, "review.md"), outbox));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    [Fact]
    public void A_granted_write_is_allowed_anywhere_on_disk_because_no_path_is_consulted()
    {
        // The limit of this gate, stated rather than assumed. Every test above drives the WITHHELD
        // path, where the target decides the verdict. When the tool is granted the hook returns
        // before it looks at any path, so `WriteFiles: true` bounds nothing — not to the outbox, not
        // to the workspace, not to this filesystem's root. #679.
        //
        // Measured live on the vendor side by `agy.plan-mode-does-not-deny-writes`: agy writes
        // outside every directory it was given, so nothing beneath AER supplies the bound either.
        using var stderr = new StringWriter();
        var somewhereElse = Path.Combine(Path.GetTempPath(), "not-the-workspace", "anything.txt");

        Assert.Equal(
            HookCheckCommand.AllowedExitCode,
            HookCheckCommand.Execute(
                new StringReader(Payload("Write", new { file_path = somewhereElse })),
                stderr, "claude:Bash", Outbox));

        // The control: the same path, the same payload, with Write withheld instead of granted. It
        // is what makes the assertion above a statement about the grant rather than about the path.
        Assert.Equal(HookCheckCommand.DeniedExitCode, Decide("Write", somewhereElse));
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

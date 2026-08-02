namespace Aer.Ui.Tests;

/// <summary>
/// #823: <c>Documents/AER Flow</c> is a real, pre-existing user folder — the rename to
/// <c>Documents/Baton</c> has to move it, not just relabel the default going forward. Both
/// polarity arms matter: an old-only folder gets moved, but a person who already has *both* (e.g.
/// ran a build that wrote the new name before this shipped) must not have either one silently
/// merged or overwritten.
/// </summary>
public class WorkspaceMigrationTests
{
    private static string NewTempRoot() => Path.Combine(Path.GetTempPath(), $"ui-workspace-migration-{Guid.NewGuid():N}");

    [Fact]
    public void LegacyFolderOnly_IsMovedToCurrentPath()
    {
        var legacy = NewTempRoot();
        var current = NewTempRoot();
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "marker.txt"), "kept");

        try
        {
            var result = WorkspaceMigration.Run(legacy, current);

            Assert.Equal(WorkspaceMigrationOutcome.Migrated, result.Outcome);
            Assert.False(Directory.Exists(legacy));
            Assert.True(Directory.Exists(current));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(current, "marker.txt")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(legacy);
            DirectoryCleanup.DeleteRecursively(current);
        }
    }

    /// <summary>
    /// #863, whose cost the catch in <see cref="WorkspaceMigration.Run(string, string)"/> records:
    /// the move is best-effort, so a failure reports and startup continues. The arm below uses a
    /// real open handle rather than a stubbed failure, because that is the condition an editor or
    /// a sync client actually creates.
    /// </summary>
    /// <summary>
    /// The cross-platform arm: something that is not a directory already occupies the destination
    /// name, so <see cref="Directory.Exists"/> does not see it and the move is attempted and
    /// fails. Windows and Linux both throw here, unlike the open-handle arm below.
    /// </summary>
    [Fact]
    public void A_move_that_cannot_complete_reports_it_instead_of_throwing_at_startup()
    {
        var legacy = NewTempRoot();
        var current = NewTempRoot();
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "marker.txt"), "kept");
        File.WriteAllText(current, "a stray file wearing the folder's name");

        try
        {
            var result = WorkspaceMigration.Run(legacy, current);

            Assert.Equal(WorkspaceMigrationOutcome.CouldNotMove, result.Outcome);
            Assert.Contains(legacy, result.Message);
            Assert.Contains(current, result.Message);
            // Nothing was half-moved: the data is still where it was, and the app can open on the
            // new path regardless.
            Assert.True(Directory.Exists(legacy));
            Assert.Equal("kept", File.ReadAllText(Path.Combine(legacy, "marker.txt")));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(legacy);
            if (File.Exists(current)) File.Delete(current);
        }
    }

    /// <summary>
    /// The condition an editor or a sync client actually creates, which is Windows-only: a handle
    /// opened with <see cref="FileShare.None"/> blocks the move there, while POSIX locking is
    /// advisory and the move simply succeeds. Asserting the Windows behaviour everywhere is what
    /// reddened this PR's first CI run on Linux -- the arm is real, its population is not.
    /// </summary>
    [Fact]
    public void An_open_handle_in_the_legacy_folder_reports_instead_of_throwing_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FileShare.None is advisory off Windows, so an open handle does not block the move here.");
        }

        var legacy = NewTempRoot();
        var current = NewTempRoot();
        Directory.CreateDirectory(legacy);
        var lockedFile = Path.Combine(legacy, "held-open.txt");
        File.WriteAllText(lockedFile, "in use");

        using var holdOpen = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        try
        {
            var result = WorkspaceMigration.Run(legacy, current);

            Assert.Equal(WorkspaceMigrationOutcome.CouldNotMove, result.Outcome);
            Assert.True(Directory.Exists(legacy));
            Assert.True(File.Exists(lockedFile));
        }
        finally
        {
            holdOpen.Dispose();
            DirectoryCleanup.DeleteRecursively(legacy);
            DirectoryCleanup.DeleteRecursively(current);
        }
    }

    [Fact]
    public void BothFoldersPresent_MovesNothingAndNamesBothPaths()
    {
        var legacy = NewTempRoot();
        var current = NewTempRoot();
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(legacy, "legacy-marker.txt"), "legacy");
        File.WriteAllText(Path.Combine(current, "current-marker.txt"), "current");

        try
        {
            var result = WorkspaceMigration.Run(legacy, current);

            Assert.Equal(WorkspaceMigrationOutcome.BothPresentNoAction, result.Outcome);
            Assert.True(Directory.Exists(legacy));
            Assert.True(Directory.Exists(current));
            Assert.True(File.Exists(Path.Combine(legacy, "legacy-marker.txt")));
            Assert.True(File.Exists(Path.Combine(current, "current-marker.txt")));
            Assert.NotNull(result.Message);
            Assert.Contains(legacy, result.Message);
            Assert.Contains(current, result.Message);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(legacy);
            DirectoryCleanup.DeleteRecursively(current);
        }
    }

    [Fact]
    public void NeitherFolderPresent_IsANoOp()
    {
        var legacy = NewTempRoot();
        var current = NewTempRoot();

        var result = WorkspaceMigration.Run(legacy, current);

        Assert.Equal(WorkspaceMigrationOutcome.NoLegacyFolder, result.Outcome);
        Assert.False(Directory.Exists(legacy));
        Assert.False(Directory.Exists(current));
    }

    /// <summary>Pin for the single-definition-site fix (#823): the folder name is defined once, on
    /// <see cref="DefaultWorkspace"/>, and both prior duplicate sites now read it — a fresh
    /// duplicate literal is the regression this guards against, not a specific string value.</summary>
    [Fact]
    public void EffectiveWorkspacePath_DerivesFromTheSingleDefaultWorkspaceDefinition()
    {
        var flow = new NewWorkflowViewModel { WorkflowName = "sample" };

        var expected = Path.Combine(DefaultWorkspace.RootPath, "sample");

        Assert.Equal(expected, flow.EffectiveWorkspacePath);
    }
}

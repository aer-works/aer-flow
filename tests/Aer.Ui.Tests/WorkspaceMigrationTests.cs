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
            if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true);
            if (Directory.Exists(current)) Directory.Delete(current, recursive: true);
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
            Directory.Delete(legacy, recursive: true);
            Directory.Delete(current, recursive: true);
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

using Aer.Ui.Tests.TestSupport;
namespace Aer.Ui.Tests;

/// <summary>
/// UI spec §3.1/§4: Local UI Configuration is a rebuildable convenience, never authoritative — a
/// room directory's own contents are. These tests exercise <see cref="LocalUiConfigurationStore"/>
/// against a temp config file, never <see cref="LocalUiConfigurationStore.CreateDefault"/>'s real
/// per-user path, so a test run never touches (or depends on) this host's actual config.
/// </summary>
public class LocalUiConfigurationStoreTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    [Fact]
    public async Task No_config_file_yet_loads_as_an_empty_list()
    {
        var store = new LocalUiConfigurationStore(NewConfigFilePath());

        var recents = await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(recents);
    }

    [Fact]
    public async Task No_theme_recorded_loads_as_null_meaning_follow_the_os()
    {
        var store = new LocalUiConfigurationStore(NewConfigFilePath());

        var theme = await store.LoadThemeAsync(TestContext.Current.CancellationToken);

        Assert.Null(theme);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("System")]
    public async Task A_recorded_theme_round_trips(string chosen)
    {
        var store = new LocalUiConfigurationStore(NewConfigFilePath());

        await store.RecordThemeAsync(chosen, TestContext.Current.CancellationToken);
        var theme = await store.LoadThemeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(chosen, theme);
    }

    [Fact]
    public async Task Recording_a_theme_leaves_the_recents_list_intact()
    {
        // Theme and recents share one config file; writing one must not clobber the other.
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-config-theme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            await store.RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);
            await store.RecordThemeAsync("Dark", TestContext.Current.CancellationToken);

            Assert.Equal("Dark", await store.LoadThemeAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                Path.GetFullPath(roomDirectory),
                Assert.Single(await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task A_recorded_directory_is_the_first_entry_on_the_next_load()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            await store.RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);

            var recents = await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(roomDirectory), Assert.Single(recents));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Recording_the_same_directory_again_moves_it_to_the_front_without_duplicating_it()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var first = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        var second = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            await store.RecordOpenedAsync(first, TestContext.Current.CancellationToken);
            await store.RecordOpenedAsync(second, TestContext.Current.CancellationToken);
            await store.RecordOpenedAsync(first, TestContext.Current.CancellationToken);

            var recents = await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken);

            Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], recents);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(first);
            DirectoryCleanup.DeleteRecursively(second);
        }
    }

    [Fact]
    public async Task A_remembered_directory_that_no_longer_exists_is_silently_dropped_on_load()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        await store.RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);
        DirectoryCleanup.DeleteRecursively(roomDirectory);

        var recents = await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(recents);
    }

    [Fact]
    public async Task A_corrupt_config_file_loads_as_an_empty_list_not_a_thrown_exception()
    {
        var configFilePath = NewConfigFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(configFilePath)!);
        await File.WriteAllTextAsync(configFilePath, "{ not valid json for a string list", TestContext.Current.CancellationToken);
        var store = new LocalUiConfigurationStore(configFilePath);

        var recents = await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(recents);
    }

    [Fact]
    public async Task No_bindings_or_template_path_remembered_yet_loads_as_null()
    {
        var store = new LocalUiConfigurationStore(NewConfigFilePath());

        Assert.Null(await store.LoadLastBindingsFilePathAsync(TestContext.Current.CancellationToken));
        Assert.Null(await store.LoadLastWorkflowTemplateFilePathAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_recorded_bindings_path_is_returned_on_the_next_load_alongside_the_recents_list()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var roomDirectory = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(roomDirectory);
        try
        {
            await store.RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);
            await store.RecordBindingsFilePathAsync("bindings.json", TestContext.Current.CancellationToken);
            await store.RecordWorkflowTemplateFilePathAsync("workflow.json", TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath("bindings.json"), await store.LoadLastBindingsFilePathAsync(TestContext.Current.CancellationToken));
            Assert.Equal(Path.GetFullPath("workflow.json"), await store.LoadLastWorkflowTemplateFilePathAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                Path.GetFullPath(roomDirectory), Assert.Single(await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken)));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(roomDirectory);
        }
    }

    [Fact]
    public async Task Recording_more_than_the_cap_keeps_only_the_most_recent_entries()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var roomDirectories = Enumerable.Range(0, 12)
            .Select(_ => Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}"))
            .ToList();
        foreach (var roomDirectory in roomDirectories)
        {
            Directory.CreateDirectory(roomDirectory);
        }

        try
        {
            foreach (var roomDirectory in roomDirectories)
            {
                await store.RecordOpenedAsync(roomDirectory, TestContext.Current.CancellationToken);
            }

            var recents = await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(10, recents.Count);
            Assert.Equal(Path.GetFullPath(roomDirectories[^1]), recents[0]);
        }
        finally
        {
            foreach (var roomDirectory in roomDirectories)
            {
                DirectoryCleanup.DeleteRecursively(roomDirectory);
            }
        }
    }

    [Fact]
    public async Task Removing_a_recorded_directory_drops_it_from_the_next_load()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var keep = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        var remove = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keep);
        Directory.CreateDirectory(remove);
        try
        {
            await store.RecordOpenedAsync(keep, TestContext.Current.CancellationToken);
            await store.RecordOpenedAsync(remove, TestContext.Current.CancellationToken);

            await store.RemoveRecentRoomDirectoryAsync(remove, TestContext.Current.CancellationToken);

            var recents = await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFullPath(keep), Assert.Single(recents));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(keep);
            DirectoryCleanup.DeleteRecursively(remove);
        }
    }

    [Fact]
    public async Task Removing_a_directory_that_was_never_recorded_is_a_no_op()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var recorded = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(recorded);
        try
        {
            await store.RecordOpenedAsync(recorded, TestContext.Current.CancellationToken);

            await store.RemoveRecentRoomDirectoryAsync(
                Path.Combine(Path.GetTempPath(), $"never-recorded-{Guid.NewGuid():N}"), TestContext.Current.CancellationToken);

            var recents = await store.LoadRecentRoomDirectoriesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFullPath(recorded), Assert.Single(recents));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(recorded);
        }
    }
}

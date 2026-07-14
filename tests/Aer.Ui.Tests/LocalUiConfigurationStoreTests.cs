namespace Aer.Ui.Tests;

/// <summary>
/// UI spec §3.1/§4: Local UI Configuration is a rebuildable convenience, never authoritative — a
/// task directory's own contents are. These tests exercise <see cref="LocalUiConfigurationStore"/>
/// against a temp config file, never <see cref="LocalUiConfigurationStore.CreateDefault"/>'s real
/// per-user path, so a test run never touches (or depends on) this host's actual config.
/// </summary>
public class LocalUiConfigurationStoreTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-config-{Guid.NewGuid():N}", "recent-task-directories.json");

    [Fact]
    public async Task No_config_file_yet_loads_as_an_empty_list()
    {
        var store = new LocalUiConfigurationStore(NewConfigFilePath());

        var recents = await store.LoadRecentTaskDirectoriesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(recents);
    }

    [Fact]
    public async Task A_recorded_directory_is_the_first_entry_on_the_next_load()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(taskDirectory);
        try
        {
            await store.RecordOpenedAsync(taskDirectory, TestContext.Current.CancellationToken);

            var recents = await store.LoadRecentTaskDirectoriesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(taskDirectory), Assert.Single(recents));
        }
        finally
        {
            Directory.Delete(taskDirectory, recursive: true);
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

            var recents = await store.LoadRecentTaskDirectoriesAsync(TestContext.Current.CancellationToken);

            Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], recents);
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public async Task A_remembered_directory_that_no_longer_exists_is_silently_dropped_on_load()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var taskDirectory = Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(taskDirectory);
        await store.RecordOpenedAsync(taskDirectory, TestContext.Current.CancellationToken);
        Directory.Delete(taskDirectory, recursive: true);

        var recents = await store.LoadRecentTaskDirectoriesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(recents);
    }

    [Fact]
    public async Task A_corrupt_config_file_loads_as_an_empty_list_not_a_thrown_exception()
    {
        var configFilePath = NewConfigFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(configFilePath)!);
        await File.WriteAllTextAsync(configFilePath, "{ not valid json for a string list", TestContext.Current.CancellationToken);
        var store = new LocalUiConfigurationStore(configFilePath);

        var recents = await store.LoadRecentTaskDirectoriesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(recents);
    }

    [Fact]
    public async Task Recording_more_than_the_cap_keeps_only_the_most_recent_entries()
    {
        var configFilePath = NewConfigFilePath();
        var store = new LocalUiConfigurationStore(configFilePath);
        var taskDirectories = Enumerable.Range(0, 12)
            .Select(_ => Path.Combine(Path.GetTempPath(), $"ui-config-task-{Guid.NewGuid():N}"))
            .ToList();
        foreach (var taskDirectory in taskDirectories)
        {
            Directory.CreateDirectory(taskDirectory);
        }

        try
        {
            foreach (var taskDirectory in taskDirectories)
            {
                await store.RecordOpenedAsync(taskDirectory, TestContext.Current.CancellationToken);
            }

            var recents = await store.LoadRecentTaskDirectoriesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(10, recents.Count);
            Assert.Equal(Path.GetFullPath(taskDirectories[^1]), recents[0]);
        }
        finally
        {
            foreach (var taskDirectory in taskDirectories)
            {
                Directory.Delete(taskDirectory, recursive: true);
            }
        }
    }
}

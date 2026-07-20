namespace Aer.Adapters.Tests;

/// <summary>
/// M23 Phase 3's per-machine profile mapping (#272): <see cref="AerProfileStore"/>'s load/save round
/// trip and its "missing file is empty, malformed file throws" distinction — see the type's own
/// remarks for why those two failure shapes are treated differently.
/// </summary>
public class AerProfileStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"aer-profiles-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Loading_a_missing_file_resolves_to_an_empty_map()
    {
        var path = TempPath();

        var profiles = await AerProfileStore.LoadAsync(path);

        Assert.Empty(profiles);
    }

    [Fact]
    public async Task Saving_then_loading_round_trips_the_map()
    {
        var path = TempPath();
        try
        {
            var original = new Dictionary<string, string>
            {
                ["myproject"] = "/home/user/dev/myproject",
                ["other"] = "/home/user/dev/other",
            };

            await AerProfileStore.SaveAsync(original, path);
            var loaded = await AerProfileStore.LoadAsync(path);

            Assert.Equal(original, loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Saving_creates_the_parent_directory_if_it_does_not_exist_yet()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"aer-profiles-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "profiles.json");
        try
        {
            await AerProfileStore.SaveAsync(new Dictionary<string, string> { ["p"] = "/x" }, path);

            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Loading_a_malformed_file_throws_rather_than_silently_resolving_to_empty()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json");

            var ex = await Assert.ThrowsAsync<ProfileStoreException>(() => AerProfileStore.LoadAsync(path));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultPath_lives_under_a_dot_aer_directory_in_the_user_profile()
    {
        Assert.EndsWith(Path.Combine(".aer", "profiles.json"), AerProfileStore.DefaultPath);
    }
}
